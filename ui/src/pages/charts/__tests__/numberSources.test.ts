import { describe, expect, it } from 'vitest';
import type { ChartSeries } from '@/api/chartTypes';
import { compileChartWhere } from '../chartWhere';
import { buildChartSql } from '../buildChartSql';
import { seriesAggregate } from '../seriesAggregate';

const series: ChartSeries = { id: 'a', aggFunc: 'avg', field: 'value', where: '', alias: '' };
const base = { domain: 'metrics' as const, chartType: 'number' as const, groupBy: [], granularity: 'auto' as const, rangeMs: 3600000 };
describe('Number source queries', () => {
  it('binds metric names and JSON filters independently per series across value, trend and samples', () => {
    const parameters = {};
    const drafts = [{ ...series, metricName: "cpu'load", metricFilters: [{ field: 'labels["host"]', value: "a'b" }] }, { ...series, id: 'b', metricName: 'memory' }];
    const compiled = compileChartWhere('metrics', drafts, [], parameters);
    for (const chartType of ['number', 'time_series', 'search'] as const) {
      const sql = buildChartSql({ ...base, chartType, seriesList: compiled, parameters });
      expect(sql).not.toContain("cpu'load");
      expect(sql).not.toContain("a'b");
      expect(sql).toContain('name = $cf');
    }
    expect(Object.values(parameters)).toEqual(expect.arrayContaining(["cpu'load", 'memory', "a'b", '$."host"']));
    expect(drafts[0].where).toBe('');
    expect(compiled[0].where).not.toBe(compiled[1].where);
  });
  it('computes Delta within metric and label identity before whole-range or bucket aggregation', () => {
    const parameters = {};
    const seriesList = compileChartWhere('metrics', [{ ...series, metricName: 'cpu', metricDelta: true }], [], parameters);
    for (const chartType of ['number', 'time_series'] as const) {
      const sql = buildChartSql({ ...base, chartType, seriesList, parameters, stableSeriesIds: chartType === 'time_series' });
      expect(sql).toContain('lag(value) OVER (PARTITION BY name, labels ORDER BY ts)');
      expect(sql).toContain('previous_value IS NULL THEN NULL');
      expect(sql).toContain('ELSE value - previous_value END');
      expect(sql).not.toContain("name = 'cpu'");
    }
    expect(() => buildChartSql({ ...base, seriesList: [{ ...series, metricDelta: true }] })).toThrow();
  });
  it('supports typed trace expressions, percentiles, Any and bounded custom aggregates', () => {
    const parameters = {};
    expect(seriesAggregate({ ...series, field: 'duration_us / 1000' }, 'traces', parameters)).toContain('NULLIF');
    expect(Object.values(parameters)).toContain(1000);
    expect(seriesAggregate({ ...series, aggFunc: 'p99', field: 'duration_us' }, 'traces', parameters)).toContain('quantile_cont');
    expect(seriesAggregate({ ...series, aggFunc: 'any' }, 'metrics', {})).toContain('any_value');
    expect(seriesAggregate({ ...series, aggFunc: 'custom', field: 'sum(value) / count(*)' }, 'metrics', {})).toContain('NULLIF');
    for (const field of ['avg(service)', 'sum(value); SELECT 1', 'read_csv(value)', 'sum(value) + unknown', "sum(value) + (SELECT 1)"]) {
      expect(() => seriesAggregate({ ...series, aggFunc: 'custom', field }, 'metrics', {})).toThrow();
    }
    expect(() => seriesAggregate({ ...series, field: 'service' }, 'traces', {})).toThrow();
    expect(seriesAggregate({ ...series, aggFunc: 'count_distinct', field: 'trace_id' }, 'traces', {})).toContain('count(DISTINCT');
  });
  it('retains project-scoped session deduplication and rejects text averages before executing', () => {
    const sql = buildChartSql({ ...base, domain: 'sessions', seriesList: [{ ...series, aggFunc: 'count', field: '' }] });
    expect(sql).toContain('GROUP BY project_key, session_id');
    expect(() => seriesAggregate({ ...series, field: 'session_id' }, 'sessions', {})).toThrow();
  });
});
