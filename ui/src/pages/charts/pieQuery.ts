import type { AggFunc, ChartSeries, ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { splitFieldList } from '@/pages/search/jsonFields';
import { buildChartSql } from './buildChartSql';
import { compileChartWhere } from './chartWhere';

export const pieGroups = (value: string) => splitFieldList(value);
export function pieAggregations(source: ChartSource): AggFunc[] {
  return source === 'sessions' ? ['count', 'count_distinct'] : ['count', 'count_distinct', 'avg', 'sum', 'min', 'max', 'p50', 'p90', 'p95', 'p99'];
}

export function compilePieQuery(source: ChartSource, series: ChartSeries, group: string, syntax: 'lucene' | 'sql', filters: { field: string; value: unknown }[], rangeMs = 0) {
  if (source === 'metrics' && series.metricName === '') throw new Error('请选择要统计的指标');
  if (!pieAggregations(source).includes(series.aggFunc)) throw new Error('该数据源不支持此饼图聚合');
  const parameters: SearchSqlParameters = {};
  const compiled = compileChartWhere(source, [{ ...series, columnExpression: true, whereSyntax: syntax }], filters, parameters);
  const input = { domain: source, chartType: 'pie' as const, seriesList: compiled, groupBy: pieGroups(group), granularity: 'auto' as const, rangeMs, parameters };
  const sql = buildChartSql(input);
  const sampleSql = buildChartSql({ ...input, chartType: 'search', groupBy: [] });
  return { sql, sampleSql, parameters };
}
