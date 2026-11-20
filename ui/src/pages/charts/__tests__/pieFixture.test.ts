import savedFixture from '../../../../../tests/SData.Store.Tests/Fixtures/pie.json';
import { describe, expect, it } from 'vitest';
import type { ChartSeries, ChartSource } from '@/api/chartTypes';
import { compilePieQuery } from '../pieQuery';

const series: ChartSeries = { id: 'pie', aggFunc: 'count', field: '', where: '', alias: '' };
const cases: { name: string; source: ChartSource; series: ChartSeries; group: string }[] = [
  { name: 'logs', source: 'logs', series, group: 'service' },
  { name: 'metric', source: 'metrics', series: { ...series, aggFunc: 'avg', field: 'value', metricName: "cpu'usage", metricFilters: [{ field: 'labels["env"]', value: "a'b" }] }, group: 'labels["host,name"]' },
  { name: 'delta', source: 'metrics', series: { ...series, aggFunc: 'sum', field: 'value', metricName: "cpu'usage", metricDelta: true }, group: 'labels["host,name"]' },
  { name: 'trace', source: 'traces', series: { ...series, aggFunc: 'p99', field: 'duration_us / 1000' }, group: 'service' },
  { name: 'session', source: 'sessions', series, group: 'domain' },
];
describe('Pie DuckDB fixtures', () => {
  it('matches the frontend queries executed by backend integration tests', () => {
    const generated = cases.map(({ name, source, series, group }) => ({ name, ...compilePieQuery(source, series, group, 'sql', []) }));
    expect(savedFixture).toEqual(generated);
  });
});
