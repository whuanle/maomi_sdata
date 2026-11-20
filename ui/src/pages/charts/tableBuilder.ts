import { bindBarWhere } from './barWhere';
import { SEARCH_FIELDS } from '@/api/search';
import type { AggFunc, ChartSeries, ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { bindChartParameter } from './formula';
import { aggExpr } from './buildChartSql';

export interface TableFilter { id: string; field: string; operator: 'eq' | 'neq' | 'gt' | 'gte' | 'lt' | 'lte' | 'contains' | 'exists' | 'missing'; value: string }
export interface TableBuilderSeries extends ChartSeries {
  filters?: TableFilter[];
  threshold?: { operator: 'gt' | 'gte' | 'lt' | 'lte'; value: string };
}
export const tableFields = (source: ChartSource) => SEARCH_FIELDS[source].filter((item) => item.kind !== 'time' && item.kind !== 'json');
export const aggregateFields = (source: ChartSource, aggregate: AggFunc) => tableFields(source).filter((item) => aggregate === 'count_distinct' || item.kind === 'number');
export const defaultGroup = (source: ChartSource) => ({ logs: 'service', traces: 'service', metrics: 'name', sessions: 'project_key', iot: 'device_key' }[source]);
export function defaultTableSeries(source: ChartSource): TableBuilderSeries {
  return { id: crypto.randomUUID(), ref: 'A', aggFunc: source === 'metrics' || source === 'iot' ? 'avg' : 'count', field: source === 'iot' ? 'num_value' : source === 'metrics' ? 'value' : '', where: '', alias: '' };
}
export function compileTableSeries(source: ChartSource, series: TableBuilderSeries[], parameters: SearchSqlParameters): ChartSeries[] {
  return series.map((item) => {
    if (item.aggFunc !== 'count' && !aggregateFields(source, item.aggFunc).some((field) => field.field === item.field)) throw new Error('请选择适用于该聚合的字段');
    const conditions = (item.filters ?? []).map((filter) => {
      const field = tableFields(source).find((field) => field.field === filter.field);
      if (!field) throw new Error('请选择有效筛选字段');
      const column = '"' + field.field + '"';
      if (filter.operator === 'exists') return column + ' IS NOT NULL';
      if (filter.operator === 'missing') return column + ' IS NULL';
      const operators = { eq: '=', neq: '<>', gt: '>', gte: '>=', lt: '<', lte: '<=', contains: 'ILIKE' };
      const operator = operators[filter.operator];
      if (!operator) throw new Error('无效筛选操作符');
      const value = field.kind === 'number' ? Number(filter.value) : filter.value;
      if (filter.value === '' || (typeof value === 'number' && !Number.isFinite(value))) throw new Error('请输入有效筛选值');
      if (filter.operator === 'contains' && field.kind !== 'string') throw new Error('包含条件仅适用于文本字段');
      const bound = bindChartParameter(parameters, filter.operator === 'contains' ? '%' + String(value).replace(/[\\%_]/g, '\\$&') + '%' : value);
      return column + ' ' + operator + ' ' + bound + (filter.operator === 'contains' ? " ESCAPE '\\'" : '');
    });
    let having = bindBarWhere(item.having ?? '', parameters);
    if (item.threshold?.value) {
      const value = Number(item.threshold.value);
      if (!Number.isFinite(value)) throw new Error('请输入有效聚合阈值');
      const operators = { gt: '>', gte: '>=', lt: '<', lte: '<=' };
      const operator = operators[item.threshold.operator];
      if (!operator) throw new Error('无效阈值操作符');
      const aggregate = /^p(50|90|95|99)$/.test(item.aggFunc)
        ? 'quantile_cont("' + item.field + '", ' + bindChartParameter(parameters, Number(item.aggFunc.slice(1)) / 100) + ')'
        : aggExpr(item.aggFunc, item.field);
      having = [having, aggregate + ' ' + operator + ' ' + bindChartParameter(parameters, value)].filter(Boolean).map((part) => '(' + part + ')').join(' AND ');
    }
    return { ...item, where: [bindBarWhere(item.where, parameters), ...conditions].filter(Boolean).map((part) => '(' + part + ')').join(' AND '), having };
  });
}
