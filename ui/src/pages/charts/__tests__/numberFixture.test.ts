import savedFixture from '../../../../../tests/SData.Store.Tests/Fixtures/number.json';
import { describe, expect, it } from 'vitest';
import type { ChartSeries, ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { buildChartSql } from '../buildChartSql';
import { compileChartWhere } from '../chartWhere';

const series: ChartSeries = { id: 'a', aggFunc: 'avg', field: 'value', where: '', alias: '' };
const cases: { name: string; domain: ChartSource; series: ChartSeries }[] = [
  { name: 'metric', domain: 'metrics', series: { ...series, metricName: "cpu'usage" } },
  { name: 'delta', domain: 'metrics', series: { ...series, aggFunc: 'sum', metricName: "cpu'usage", metricDelta: true } },
  { name: 'custom', domain: 'metrics', series: { ...series, aggFunc: 'custom', field: 'sum(value) / count(*)', metricName: "cpu'usage" } },
  { name: 'trace', domain: 'traces', series: { ...series, aggFunc: 'p99', field: 'duration_us / 1000', columnExpression: true } },
  { name: 'session', domain: 'sessions', series: { ...series, aggFunc: 'count', field: '', where: "domain = 'new.example'" } },
];
describe('Number DuckDB fixtures', () => {
  it('matches the generated queries executed by backend integration tests', () => {
    const generated = cases.map(({ name, domain, series }) => {
      const parameters: SearchSqlParameters = {};
      const seriesList = compileChartWhere(domain, [series], [], parameters);
      const sql = buildChartSql({ domain, chartType: 'number', seriesList, groupBy: [], granularity: 'auto', rangeMs: 300000, parameters });
      return { name, sql, parameters };
    });
    expect(savedFixture).toEqual(generated);
  });
});
