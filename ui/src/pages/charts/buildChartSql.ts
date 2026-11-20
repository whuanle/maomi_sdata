import type { ChartSource, ChartType, AggFunc, Granularity, ChartSeries, ChartFormula } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import type { HeatmapConfig } from '@/api/chartTypes';
import { buildHeatmapSql } from './heatmap';
import { buildTableSql } from './tableQuery';
import { buildBarSql } from './barQuery';
import { buildPatternSql } from './patterns';
import { seriesAggregate } from './seriesAggregate';
import { pieGroupColumns } from './pieGroups';
import { FormulaError, bindChartParameter, formulaSql, parseFormula, seriesRef } from './formula';
export type { ChartSource, ChartType, AggFunc, Granularity, ChartSeries } from '@/api/chartTypes';

/** 列名白名单校验并加引号（拒绝引号注入与非法标识符）. */
function quoteCol(name: string): string {
  const trimmed = name.trim();
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(trimmed)) {
    throw new Error(`非法列名：${name}`);
  }

  return `"${trimmed}"`;
}

/** 聚合表达式. */
export function aggExpr(aggFunc: AggFunc, field: string): string {
  switch (aggFunc) {
    case 'count':
      return 'count(*)';
    case 'count_distinct':
      return `count(DISTINCT ${quoteCol(field)})`;
    case 'p50': case 'p90': case 'p95': case 'p99':
      return `quantile_cont(${quoteCol(field)}, ${{ p50: 0.5, p90: 0.9, p95: 0.95, p99: 0.99 }[aggFunc]})`;
    case 'avg': case 'sum': case 'min': case 'max':
      return `${aggFunc}(${quoteCol(field)})`;
    default:
      throw new Error('不支持的聚合函数');
  }
}

/** 粒度 → time_bucket 间隔表达式. */
const GRAN_INTERVAL: Record<Exclude<Granularity, 'auto'>, string> = {
  '1m': "INTERVAL '1 minute'",
  '5m': "INTERVAL '5 minutes'",
  '15m': "INTERVAL '15 minutes'",
  '30m': "INTERVAL '30 minutes'",
  '1h': "INTERVAL '1 hour'",
  '6h': "INTERVAL '6 hours'",
  '1d': "INTERVAL '1 day'",
};

/** 解析粒度（auto 按时间窗自适应，与搜索页直方图同规则）. */
export function resolveGranularity(granularity: Granularity, rangeMs: number): Exclude<Granularity, 'auto'> {
  if (granularity !== 'auto') {
    return granularity;
  }

  if (rangeMs <= 60 * 60_000) {
    return '1m';
  }

  if (rangeMs <= 6 * 60 * 60_000) {
    return '5m';
  }

  if (rangeMs <= 24 * 60 * 60_000) {
    return '15m';
  }

  if (rangeMs <= 48 * 60 * 60_000) {
    return '30m';
  }

  if (rangeMs <= 7 * 24 * 60 * 60_000) {
    return '1h';
  }

  return '1d';
}

/** 各数据域事件列（Search 模式原始行）. */
export const DOMAIN_COLUMNS: Record<ChartSource, string[]> = {
  logs: ['ts', 'id', 'service', 'severity', 'body', 'trace_id', 'span_id', 'source'],
  metrics: ['ts', 'name', 'value', 'labels', 'kind'],
  iot: ['ts', 'device_key', 'signal', 'num_value', 'str_value', 'quality'],
  sessions: ['ts', 'project_key', 'session_id', 'user_agent', 'domain'],
  traces: ['ts', 'service', 'name', 'duration_us', 'status', 'trace_id', 'span_id', 'parent_span_id', 'attributes', 'kind', 'resource', 'events'],
};

/** 序列默认标签. */
export function seriesLabel(series: ChartSeries): string {
  if (series.alias.trim().length > 0) {
    return series.alias.trim();
  }

  return series.aggFunc === 'count' ? 'count' : `${series.aggFunc}(${series.field.trim()})`;
}

export interface BuildChartSqlInput {
  patternExpression?: string;
  heatmap?: HeatmapConfig;
  domain: ChartSource;
  chartType: ChartType;
  seriesList: ChartSeries[];
  groupBy: string[];
  granularity: Granularity;
  rangeMs: number;
  bucketParameter?: boolean;
  stableSeriesIds?: boolean;
  formulas?: ChartFormula[];
  parameters?: SearchSqlParameters;
  tableOrderBy?: string;
  tableQuery?: import('@/api/chartTypes').TableQuerySettings;
  tableProbe?: boolean;
  orderBy?: string;
  seriesLimit?: number;
}

function aggregate(series: ChartSeries, input: BuildChartSqlInput): string {
  return series.columnExpression || series.aggFunc === 'any' || series.aggFunc === 'custom'
    ? seriesAggregate(series, input.domain, input.parameters ?? {})
    : aggExpr(series.aggFunc, series.field);
}

/** Delta 在每个指标/标签序列内计算；窗口首点无差值，不推断未存储的单调性/重置元数据. */
function seriesTable(series: ChartSeries, input: BuildChartSqlInput, table: string): string {
  if (!series.metricDelta) return table;
  if (input.domain !== 'metrics' || !series.metricName || series.aggFunc === 'count' || series.aggFunc === 'count_distinct' || series.aggFunc === 'custom') throw new Error('Delta 需要选择单个指标及数值聚合');
  const filter = series.where.trim() ? ' AND (' + series.where + ')' : '';
  return "(SELECT * EXCLUDE (value, previous_value), CASE WHEN previous_value IS NULL THEN NULL ELSE value - previous_value END AS value FROM (SELECT *, lag(value) OVER (PARTITION BY name, labels ORDER BY ts) AS previous_value FROM " + table + " WHERE ts >= $start AND ts < $end" + filter + "))";
}

/** 输入聚合按完整分组元组透视，公式在同一坐标上计算. */
function buildFormulaSql(input: BuildChartSqlInput, table: string, interval: string): string {
  const parameters = input.parameters;
  if (!parameters || input.seriesList.length > 12 || (input.formulas?.length ?? 0) > 8) throw new FormulaError('formulaComplex');
  const bind = (value: string | number) => bindChartParameter(parameters, value);
  const refs = input.seriesList.map((series, index) => series.ref ?? seriesRef(index));
  const bucketed = input.chartType === 'time_series' || input.chartType === 'heatmap';
  const groups = input.chartType === 'number' ? [] : input.groupBy.map(quoteCol);
  const coordinates = [...(bucketed ? ['ts_bucket'] : []), ...groups];
  const prefix = coordinates.length ? coordinates.join(', ') + ', ' : '';
  const branches = input.seriesList.map((series, index) => {
    const select = [...(bucketed ? [`time_bucket(${interval}, ts) AS ts_bucket`] : []), ...groups];
    const group = coordinates.length ? ` GROUP BY ${coordinates.join(', ')}` : '';
    const filter = !series.metricDelta && series.where.trim() ? ` AND (${series.where})` : '';
    return `SELECT ${select.length ? select.join(', ') + ', ' : ''}${index} AS input_index, CAST(${aggregate(series, input)} AS DOUBLE) AS value FROM ${seriesTable(series, input, table)} WHERE ts >= $start AND ts < $end${filter}${group}`;
  });
  const pivot = input.seriesList.map((_, index) => `max(CASE WHEN input_index = ${index} AND isfinite(value) THEN value END) AS v${index}`).join(', ');
  const outputs = input.seriesList.map((series, index) =>
    `SELECT ${prefix}${bind(series.id)} AS __series_id, ${bind(series.alias.trim() || refs[index])} AS series, false AS __formula, v${index} AS value FROM aligned`);
  for (const formula of input.formulas ?? []) {
    const expression = formulaSql(parseFormula(formula.expression, refs), refs, bind);
    outputs.push(`SELECT ${prefix}${bind(formula.id)} AS __series_id, ${bind(formula.alias.trim() || formula.expression)} AS series, true AS __formula, ${expression} AS value FROM aligned`);
  }
  const order = bucketed ? 'ts_bucket' : 'value DESC NULLS LAST';
  const limit = 10001;
  return `WITH inputs AS (${branches.join('\nUNION ALL\n')}), aligned AS (SELECT ${prefix}${pivot} FROM inputs${coordinates.length ? ' GROUP BY ' + coordinates.join(', ') : ''}), calculated AS (${outputs.join('\nUNION ALL\n')}) SELECT * REPLACE (CASE WHEN isfinite(value) THEN value END AS value) FROM calculated ORDER BY ${order} LIMIT ${limit}`;
}

/**
 * 由 Builder 状态生成 DuckDB SQL（{{logs}}/{{traces}} 表占位符 + $start/$end 时间参数，
 * 经 POST /api/analytics/search 执行：服务端展开按天表并参数绑定，仅允许只读单语句）.
 */
export function buildChartSql(input: BuildChartSqlInput): string {
  if (!Object.prototype.hasOwnProperty.call(DOMAIN_COLUMNS, input.domain)) throw new Error('未知数据源');
  if (input.chartType !== 'pie') for (const field of input.groupBy) if (!DOMAIN_COLUMNS[input.domain].includes(field)) throw new Error('未知分组字段：' + field);
  if (input.seriesList.length > 12) throw new FormulaError('formulaComplex');
  for (const series of input.seriesList) if (series.aggFunc !== 'count' && !['bar', 'table', 'search', 'patterns', 'heatmap'].includes(input.chartType) && !series.columnExpression && !['any', 'custom'].includes(series.aggFunc) && !DOMAIN_COLUMNS[input.domain].includes(series.field)) throw new Error('未知聚合字段：' + series.field);
  const rawTable = '{{' + input.domain + '}}';
  const table = input.domain === 'sessions'
    ? `(SELECT project_key, session_id, min(ts) AS ts, arg_max(user_agent, ts) AS user_agent, arg_max(domain, ts) AS domain FROM ${rawTable} WHERE ts >= $start AND ts < $end GROUP BY project_key, session_id)`
    : rawTable;
  const pieColumns = input.chartType === 'pie' ? pieGroupColumns(input.domain, input.groupBy, input.parameters ?? {}) : null;
  const groupCols = pieColumns ? pieColumns.map((column) => column.group) : input.groupBy.map((col) => quoteCol(col));
  const groupSelect = groupCols.length > 0 ? `${(pieColumns ? pieColumns.map((column) => column.select) : groupCols).join(', ')}, ` : '';
  const groupList = groupCols.length > 0 ? `, ${groupCols.join(', ')}` : '';
  const rangeWhere = 'WHERE ts >= $start AND ts < $end';
  const interval = input.bucketParameter ? 'CAST($chart_bucket AS INTERVAL)' : GRAN_INTERVAL[resolveGranularity(input.granularity, input.rangeMs)];

  if (input.chartType === 'heatmap' && input.heatmap) {
    if (!input.parameters) throw new Error('热力图需要查询参数');
    return buildHeatmapSql(input.domain, input.heatmap, input.seriesList[0]?.where ?? '', interval, input.parameters);
  }

  if (input.chartType === 'search') {
    const filters = input.seriesList.map((series) => series.where.trim());
    const filter = filters.some((value) => !value) ? '' : filters.map((value) => `(${value})`).join(' OR ');
    const extra = filter ? ` AND (${filter})` : '';
    const columns = DOMAIN_COLUMNS[input.domain].join(', ');
    return `SELECT ${columns}\nFROM ${table}\n${rangeWhere}${extra}\nORDER BY ts DESC\nLIMIT 500`;
  }

  if (input.chartType === 'bar') return buildBarSql(input, table);
  if (input.chartType === 'table') return buildTableSql(input, table);

  if (input.stableSeriesIds || (input.formulas?.length ?? 0) > 0 || (input.chartType === 'number' && input.seriesList.length > 1)) {
    return buildFormulaSql(input, table, interval);
  }

  if (input.chartType === 'number') {
    const series = input.seriesList[0];
    const filter = !series.metricDelta && series?.where.trim();
    const extra = filter ? ` AND (${filter})` : '';
    return `SELECT ${aggregate(series, input)} AS value\nFROM ${seriesTable(series, input, table)}\n${rangeWhere}${extra}`;
  }

  if (input.chartType === 'patterns') {
    if (!input.parameters) throw new Error('模式查询需要绑定参数');
    return buildPatternSql({ domain: input.domain, expression: input.patternExpression, where: input.seriesList[0]?.where, parameters: input.parameters });
  }

  // 每个序列一个子查询：ts_bucket(可选) / series 标签(多序列时) / 分组列(可选) / value
  // （number/search/patterns 已在前面的分支返回）
  const bucketed = input.chartType === 'time_series' || input.chartType === 'heatmap';
  const multi = input.seriesList.length > 1;
  const needsSeriesCol = multi;
  const limit = 10_000;

  const branches = input.seriesList.map((series) => {
    const filter = !series.metricDelta && series.where.trim();
    const extra = filter ? ` AND (${filter})` : '';
    if (needsSeriesCol && !input.parameters) throw new Error('多序列需要绑定参数');
    const label = needsSeriesCol ? bindChartParameter(input.parameters!, seriesLabel(series)) : '';
    const agg = aggregate(series, input);
    const sourceTable = seriesTable(series, input, table);

    if (bucketed) {
      const bucket = `time_bucket(${interval}, ts) AS ts_bucket`;
      if (needsSeriesCol) {
        return `SELECT ${bucket}, ${label} AS series${groupList}, ${agg} AS value\nFROM ${sourceTable}\n${rangeWhere}${extra}\nGROUP BY ts_bucket${groupList}`;
      }

      if (groupCols.length > 0) {
        return `SELECT ${bucket}, ${groupSelect}${agg} AS value\nFROM ${sourceTable}\n${rangeWhere}${extra}\nGROUP BY ts_bucket${groupList}`;
      }

      return `SELECT ${bucket}, ${agg} AS value\nFROM ${sourceTable}\n${rangeWhere}${extra}\nGROUP BY ts_bucket`;
    }

    // 非时序聚合（table/bar/pie）：按分组列聚合
    if (groupCols.length === 0) {
      return `SELECT ${needsSeriesCol ? `${label} AS series, ` : ''}${agg} AS value\nFROM ${sourceTable}\n${rangeWhere}${extra}`;
    }

    return `SELECT ${needsSeriesCol ? `${label} AS series, ` : ''}${groupSelect}${agg} AS value\nFROM ${sourceTable}\n${rangeWhere}${extra}\nGROUP BY ${groupCols.join(', ')}`;
  });

  const orderBy = bucketed ? 'ts_bucket' : 'value DESC';
  return `SELECT * FROM (\n${branches.join('\nUNION ALL\n')}\n) ORDER BY ${orderBy} LIMIT ${limit}`;
}
