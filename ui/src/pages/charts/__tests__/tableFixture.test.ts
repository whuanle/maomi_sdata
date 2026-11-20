import savedFixture from '../../../../../tests/SData.Store.Tests/Fixtures/table.json';
import { describe, expect, it } from 'vitest';
import type { SearchSqlParameters } from '@/api/types';
import { buildChartSql, type BuildChartSqlInput } from '../buildChartSql';

const base: BuildChartSqlInput = {
  domain: 'traces', chartType: 'table', granularity: 'auto', rangeMs: 300000,
  seriesList: [
    { id: 'total', ref: 'A', aggFunc: 'count', field: '', alias: 'requests', where: '' },
    { id: 'latency', ref: 'B', aggFunc: 'p95', field: 'duration_us', alias: 'p95, "latency"', where: '' },
    { id: 'errors', ref: 'C', aggFunc: 'count', field: '', alias: 'errors', where: 'status = $status', having: 'count(*) > 0' },
  ], groupBy: ['service', 'name'],
  formulas: [{ id: 'ratio', alias: 'error %', expression: 'C / A * 100' }],
  tableQuery: { topN: 1, sort: [{ columnId: 'latency', direction: 'desc' }] },
};
function fixture() {
  return [
    { name: 'slowest', ...base },
    { name: 'errors', ...base, tableQuery: { topN: 2, sort: [{ columnId: 'errors', direction: 'desc' as const }] } },
    { name: 'scalar', ...base, groupBy: [], tableQuery: { topN: 10, sort: [] } },
  ].map(({ name, ...input }) => {
    const parameters: SearchSqlParameters = { status: 'error' };
    const sql = buildChartSql({ ...input, tableProbe: false, parameters });
    return { name, sql, parameters };
  });
}
describe('Table DuckDB fixture', () => {
  it('keeps backend execution fixtures identical to frontend generated SQL', () => {
    const generated = fixture();
    expect(savedFixture).toEqual(generated);
  });
});
