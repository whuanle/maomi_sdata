import { describe, expect, it } from 'vitest';
import type { ChartSeries } from '@/api/chartTypes';
import { compilePieQuery, pieGroups, pieAggregations } from '../pieQuery';

const series: ChartSeries = { id: 'pie', aggFunc: 'count', field: '', where: '', alias: '' };
describe('Pie source queries', () => {
  it('binds the selected metric, label filters and JSON categories for chart and samples', () => {
    const query = compilePieQuery('metrics', { ...series, aggFunc: 'sum', field: 'value', metricName: "cpu'usage", metricFilters: [{ field: 'labels["env"]', value: "a'b" }] }, 'labels["host,name"]', 'sql', []);
    expect(query.sql).toContain('json_extract_string');
    expect(query.sql).toContain('AS "labels[""host,name""]"');
    expect(query.sql).not.toContain("cpu'usage");
    expect(query.sampleSql).toContain('name = $cf');
    expect(Object.values(query.parameters)).toEqual(expect.arrayContaining(["cpu'usage", "a'b", '$."host,name"']));
    expect(pieGroups('labels["host,name"], kind')).toEqual(['labels["host,name"]', 'kind']);
  });
  it('supports trace percentiles with duration conversion and session-scoped deduplication', () => {
    const trace = compilePieQuery('traces', { ...series, aggFunc: 'p99', field: 'duration_us / 1000', where: 'service:api' }, 'service', 'lucene', []);
    expect(trace.sql).toContain('quantile_cont');
    expect(Object.values(trace.parameters)).toEqual(expect.arrayContaining([1000, 0.99, 'api']));
    const session = compilePieQuery('sessions', series, 'domain', 'lucene', []);
    expect(session.sql).toContain('GROUP BY project_key, session_id');
    expect(session.sql).toContain('arg_max(domain, ts)');
    expect(pieAggregations('sessions')).toEqual(['count', 'count_distinct']);
  });
  it('rejects missing metrics, reserved or duplicate groups and invalid numeric columns', () => {
    expect(() => compilePieQuery('metrics', { ...series, metricName: '' }, '', 'sql', [])).toThrow('请选择');
    for (const group of ['value', 'kind,kind', 'labels["x"]; DROP TABLE metrics']) expect(() => compilePieQuery('metrics', { ...series, metricName: 'cpu' }, group, 'sql', [])).toThrow();
    expect(() => compilePieQuery('sessions', { ...series, aggFunc: 'avg', field: 'domain' }, '', 'sql', [])).toThrow();
    expect(() => compilePieQuery('logs', { ...series, aggFunc: 'avg', field: 'service' }, '', 'sql', [])).toThrow();
    expect(compilePieQuery('metrics', { ...series, aggFunc: 'avg', field: 'value' }, 'kind', 'sql', []).sql).toContain('avg(');
  });
  it('computes metric delta within each label identity', () => {
    const query = compilePieQuery('metrics', { ...series, aggFunc: 'sum', field: 'value', metricName: 'cpu', metricDelta: true }, 'labels["host"]', 'sql', []);
    expect(query.sql).toContain('PARTITION BY name, labels ORDER BY ts');
    expect(query.sql).toContain('ELSE value - previous_value END');
  });
});
