import type { ChartSeries, ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { buildFilterConditionSql, compileLucene } from '@/pages/search/lucene';
import { bindBarWhere } from './barWhere';
import { bindChartParameter } from './formula';

/** 原始草稿只在执行边界编译；所有序列与按值过滤共用参数空间. */
export function compileChartWhere(domain: ChartSource, series: ChartSeries[], filters: { field: string; value: unknown }[], parameters: SearchSqlParameters): ChartSeries[] {
  const compiled = series.map((item) => {
    const where = item.whereSyntax === 'lucene' ? compileLucene(item.where, domain, parameters).sql : bindBarWhere(item.where, parameters, true);
    const metric = domain === 'metrics' && item.metricName ? 'name = ' + bindChartParameter(parameters, item.metricName) : '';
    const attributes = domain === 'metrics' ? (item.metricFilters ?? []).map(({ field, value }) => buildFilterConditionSql(field, 'eq', value, domain, parameters)) : [];
    return { ...item, where: metric || attributes.length ? [metric, where, ...attributes].filter(Boolean).map((condition) => `(${condition})`).join(' AND ') : where };
  });
  const conditions = filters.map(({ field, value }) => buildFilterConditionSql(field, value == null ? 'nexists' : 'eq', typeof value === 'object' ? JSON.stringify(value) : String(value ?? ''), domain, parameters));
  return compiled.map((item) => ({ ...item, where: [item.where, ...conditions].filter(Boolean).map((condition) => `(${condition})`).join(' AND ') }));
}
