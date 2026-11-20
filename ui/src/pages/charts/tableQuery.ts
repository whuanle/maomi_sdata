import { SEARCH_FIELDS } from '@/api/search';
import type { TableColumnIdentity } from '@/api/chartTypes';
import type { BuildChartSqlInput } from './buildChartSql';
import { aggExpr, seriesLabel } from './buildChartSql';
import { bindChartParameter, formulaSql, parseFormula, seriesRef } from './formula';

export function tableMetricNames(input: Pick<BuildChartSqlInput, 'seriesList' | 'formulas' | 'groupBy'>): string[] {
  const used = new Set(input.groupBy.map((name) => name.toLowerCase()));
  return [...input.seriesList.map(seriesLabel), ...(input.formulas ?? []).map((item) => item.alias.trim() || item.expression)].map((label) => {
    let name = label;
    let index = 2;
    while (used.has(name.toLowerCase()) || name.startsWith('__')) name = `${label.replace(/^_+/, '')} (${index++})`;
    used.add(name.toLowerCase());
    return name;
  });
}

export function tableColumns(input: Pick<BuildChartSqlInput, 'seriesList' | 'formulas' | 'groupBy'>): TableColumnIdentity[] {
  return [...input.groupBy.map((name) => ({ id: 'group:' + name, name, kind: 'group' as const })),
    ...tableMetricNames(input).map((name, index) => ({
      id: index < input.seriesList.length ? input.seriesList[index].id : input.formulas![index - input.seriesList.length].id,
      name, kind: 'metric' as const,
    }))];
}

const identifier = (name: string) => '"' + name.replace(/"/g, '""') + '"';

/** 表格先按完整维度对齐，再生成指标宽列；别名不作为指标身份. */
export function buildTableSql(input: BuildChartSqlInput, table: string): string {
  if (!input.seriesList.length || input.seriesList.length > 12 || (input.formulas?.length ?? 0) > 8) throw new Error('表格最多支持 12 个序列和 8 个公式');
  const fields = SEARCH_FIELDS[input.domain];
  for (const group of input.groupBy) if (!fields.some((field) => field.field === group && field.kind !== 'json' && field.kind !== 'time')) throw new Error('未知分组字段：' + group);
  for (const series of input.seriesList) {
    if (!['count', 'count_distinct', 'avg', 'sum', 'min', 'max', 'p50', 'p90', 'p95', 'p99'].includes(series.aggFunc)) throw new Error('不支持的聚合函数');
    if (series.aggFunc !== 'count' && !fields.some((field) => field.field === series.field && (series.aggFunc === 'count_distinct' ? field.kind !== 'json' : field.kind === 'number'))) throw new Error('请选择适用于该聚合的字段');
  }
  const groups = [...new Set(input.groupBy)].map(identifier);
  const prefix = groups.length ? groups.join(', ') + ', ' : '';
  const names = tableMetricNames(input);
  const refs = input.seriesList.map((item, index) => item.ref ?? seriesRef(index));
  const parameters = input.parameters ?? {};
  const bind = (value: number) => bindChartParameter(parameters, value);
  const branches = input.seriesList.map((series, index) => {
    if (series.having && /;|--|\/\*/.test(series.having)) throw new Error('聚合后条件仅允许单个 SQL 表达式');
    const where = series.where.trim() ? ` AND (${series.where})` : '';
    const having = series.having?.trim() ? ` HAVING (${series.having})` : '';
    const percentile = /^p(50|90|95|99)$/.test(series.aggFunc);
    if (percentile && !input.parameters) throw new Error('分位数需要查询参数');
    if (percentile && !/^[A-Za-z_][A-Za-z0-9_]*$/.test(series.field)) throw new Error('非法聚合字段');
    const aggregation = percentile ? `quantile_cont(${identifier(series.field)}, ${bind(Number(series.aggFunc.slice(1)) / 100)})` : aggExpr(series.aggFunc, series.field);
    return `SELECT ${prefix}${index} AS input_index, CAST(${aggregation} AS DOUBLE) AS value FROM ${table} WHERE ts >= $start AND ts < $end${where}${groups.length ? ' GROUP BY ' + groups.join(', ') : ''}${having}`;
  });
  const pivot = input.seriesList.map((_, index) => `max(CASE WHEN input_index = ${index} AND isfinite(value) THEN value END) AS v${index}`).join(', ');
  const values = input.seriesList.map((_, index) => `v${index}`);
  if (input.formulas?.length && !input.parameters) throw new Error('表格公式需要查询参数');
  for (const formula of input.formulas ?? []) values.push(formulaSql(parseFormula(formula.expression, refs), refs, (value) => bindChartParameter(parameters, value)));
  const output = values.map((_, index) => `CASE WHEN isfinite(v${index}) THEN v${index} END AS ${identifier(names[index])}`);
  const identities = tableColumns(input);
  const structuredOrder = input.tableQuery?.sort.map((sort) => {
    const column = identities.find((item) => item.id === sort.columnId);
    if (!column || !['asc', 'desc'].includes(sort.direction)) throw new Error('非法排序列');
    return identifier(column.name) + ' ' + sort.direction.toUpperCase() + ' NULLS LAST';
  });
  const allowed = [...input.groupBy, ...names];
  const order = structuredOrder?.length ? structuredOrder.join(', ') : input.tableOrderBy?.trim()
    ? input.tableOrderBy.split(',').map((part) => {
      const match = part.trim().match(/^(?:"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))(?:\s+(ASC|DESC))?$/i);
      if (!match) throw new Error('排序格式：列名或序列引用 + ASC/DESC，多个排序用逗号分隔');
      const raw = match[1] ?? match[2];
      const refIndex = refs.indexOf(raw);
      const name = allowed.includes(raw) ? raw : refIndex >= 0 ? names[refIndex] : '';
      if (!name) throw new Error(`未知排序列：${raw}`);
      return `${identifier(name)} ${(match[3] ?? 'DESC').toUpperCase()} NULLS LAST`;
    }).join(', ')
    : `${identifier(names[0])} DESC NULLS LAST`;
  const topN = input.tableQuery?.topN;
  if (topN != null && (!Number.isInteger(topN) || topN < 1 || topN > 500)) throw new Error('Top N 必须是 1–500 的整数');
  if (topN != null && !input.parameters) throw new Error('Top N 需要查询参数');
  const limit = topN == null ? '501' : bind(topN + (input.tableProbe === false ? 0 : 1));
  const tieBreak = groups.length ? ', ' + groups.map((group) => group + ' ASC NULLS LAST').join(', ') : '';
  return `WITH inputs AS (${branches.join('\nUNION ALL\n')}), aligned AS (SELECT ${prefix}${pivot} FROM inputs${groups.length ? ' GROUP BY ' + groups.join(', ') : ''}) SELECT ${prefix}${output.join(', ')} FROM (SELECT ${prefix}${values.map((value, index) => `${value} AS v${index}`).join(', ')} FROM aligned) calculated ORDER BY ${order}${tieBreak} LIMIT ${limit}`;
}
