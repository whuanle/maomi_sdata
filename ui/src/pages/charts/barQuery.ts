import { SEARCH_FIELDS } from '@/api/search';
import type { ChartSource } from '@/api/chartTypes';
import type { BuildChartSqlInput } from './buildChartSql';
import { bindChartParameter } from './formula';
import { barColumn } from './barColumn';

export const BAR_FIELDS: Record<ChartSource, { field: string; kind: string }[]> = {
  iot: SEARCH_FIELDS.iot.filter((item) => item.kind !== 'time'),
  logs: SEARCH_FIELDS.logs.filter((item) => item.kind !== 'time' && item.kind !== 'json'),
  traces: SEARCH_FIELDS.traces.filter((item) => item.kind !== 'time' && item.kind !== 'json'),
  metrics: [{ field: 'name', kind: 'string' }, { field: 'value', kind: 'number' }],
  sessions: ['project_key', 'session_id', 'user_agent', 'domain'].map((field) => ({ field, kind: 'string' })),
};

/** 分类 Top N 在数据库内完成；排序仅引用校验后的输出列. */
export function buildBarSql(input: BuildChartSqlInput, table: string): string {
  if (input.seriesList.length !== 1 || input.formulas?.length) throw new Error('Bar 仅支持一个聚合序列');
  if (!input.parameters) throw new Error('Bar 需要查询参数');
  const series = input.seriesList[0];
  const fields = BAR_FIELDS[input.domain];
  const quote = (field: string) => {
    if (!fields.some((item) => item.field === field)) throw new Error('未知分类字段：' + field);
    return '"' + field + '"';
  };
  if (input.groupBy.includes('value')) throw new Error('value 为聚合结果保留列，请在 SQL 模式中为分类列设置别名');
  const groups = [...new Set(input.groupBy)].map(quote);
  if (!['count', 'count_distinct', 'avg', 'sum', 'min', 'max', 'p50', 'p90', 'p95', 'p99'].includes(series.aggFunc)) throw new Error('不支持的聚合函数');
  const column = series.aggFunc === 'count' ? '*' : barColumn(series.field, input.domain, input.parameters, series.aggFunc === 'count_distinct');
  const percentile = /^p(50|90|95|99)$/.test(series.aggFunc);
  const aggregate = series.aggFunc === 'count_distinct' ? 'count(DISTINCT ' + column + ')'
    : percentile ? 'quantile_cont(' + column + ', ' + bindChartParameter(input.parameters, Number(series.aggFunc.slice(1)) / 100) + ')'
    : series.aggFunc + '(' + column + ')';
  const match = (input.orderBy || 'value DESC').match(/^([A-Za-z_][A-Za-z0-9_]*) (ASC|DESC)$/);
  if (!match || (match[1] !== 'value' && !input.groupBy.includes(match[1]))) throw new Error('排序仅支持聚合值或分组字段');
  const order = (match[1] === 'value' ? '"value"' : quote(match[1])) + ' ' + match[2] + ' NULLS LAST';
  const ties = groups.map((group) => group + ' ASC NULLS LAST');
  const topOrder = ['value DESC NULLS LAST', ...ties].join(', ');
  const prefix = groups.length ? groups.join(', ') + ', ' : '';
  const filter = series.where.trim() ? ' AND (' + series.where + ')' : '';
  const aggregated = 'SELECT ' + prefix + aggregate + ' AS value FROM ' + table + ' WHERE ts >= $start AND ts < $end' + filter + (groups.length ? ' GROUP BY ' + groups.join(', ') : '');
  const limit = input.seriesLimit;
  if (limit != null && (!Number.isInteger(limit) || limit < 1 || limit > 10000)) throw new Error('分类数量必须为 1–10000');
  const selected = limit == null ? 'SELECT * FROM aggregated' : 'SELECT * FROM aggregated ORDER BY ' + topOrder + ' LIMIT ' + bindChartParameter(input.parameters, limit);
  return 'WITH aggregated AS (' + aggregated + '), selected AS (' + selected + ') SELECT * FROM selected ORDER BY ' + [order, ...ties].join(', ') + ' LIMIT 10001';
}
