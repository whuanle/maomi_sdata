import type { ChartSeries, ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { barColumn } from './barColumn';
import { bindChartParameter, formulaSql, parseFormula, seriesRef } from './formula';

/** Number 的 Column 与自定义聚合只接受白名单函数和算术，复用 Bar 的字段/JSON 参数编译. */
export function seriesAggregate(series: ChartSeries, source: ChartSource, parameters: SearchSqlParameters): string {
  if (series.aggFunc === 'custom') {
    if (series.field.length > 512) throw new Error('聚合表达式过长');
    const aggregates: string[] = [];
    const expression = series.field.replace(/\b(count_distinct|count|avg|sum|min|max|p50|p90|p95|p99|any)\s*\(([^()]*)\)/gi, (_, fn: string, column: string) => {
      const ref = seriesRef(aggregates.length);
      aggregates.push(seriesAggregate({ ...series, aggFunc: fn.toLowerCase() as ChartSeries['aggFunc'], field: column.trim(), columnExpression: true }, source, parameters));
      return ref;
    });
    if (!aggregates.length) throw new Error('请输入受支持的聚合表达式');
    const refs = aggregates.map((_, index) => seriesRef(index));
    let sql = formulaSql(parseFormula(expression, refs), refs, (value) => bindChartParameter(parameters, value));
    sql = sql.replace(/\bv(\d+)\b/g, (_, index: string) => '(' + aggregates[Number(index)] + ')');
    return sql;
  }
  if (series.aggFunc === 'count') {
    if (series.field && series.field !== '*' && series.columnExpression) throw new Error('Count 仅支持 count(*)');
    return 'count(*)';
  }
  const column = barColumn(series.field, source, parameters, series.aggFunc === 'count_distinct');
  if (series.aggFunc === 'count_distinct') return 'count(DISTINCT ' + column + ')';
  if (/^p(50|90|95|99)$/.test(series.aggFunc)) return 'quantile_cont(' + column + ', ' + bindChartParameter(parameters, Number(series.aggFunc.slice(1)) / 100) + ')';
  if (series.aggFunc === 'any') return 'any_value(' + column + ')';
  if (!['avg', 'sum', 'min', 'max'].includes(series.aggFunc)) throw new Error('不支持的聚合函数');
  return series.aggFunc + '(' + column + ')';
}
