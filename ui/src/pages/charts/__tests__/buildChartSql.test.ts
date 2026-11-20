import { describe, expect, it } from 'vitest';
import { buildChartSql, resolveGranularity, type BuildChartSqlInput } from '../buildChartSql';

const base: BuildChartSqlInput = { domain: 'logs', chartType: 'time_series', seriesList: [{ id: 'a', aggFunc: 'count', field: '', where: '', alias: '' }], groupBy: [], granularity: 'auto', rangeMs: 3600000 };
describe('Chart SQL', () => {
  it('uses the selected source and keeps time parameters', () => {
    for (const domain of ['logs', 'metrics', 'sessions', 'traces'] as const) {
      const sql = buildChartSql({ ...base, domain });
      expect(sql).toContain(`{{${domain}}}`);
      expect(sql).toContain('$start');
      expect(sql).toContain('$end');
    }
  });
  it('deduplicates sessions within each project', () => {
    expect(buildChartSql({ ...base, domain: 'sessions' })).toContain('GROUP BY project_key, session_id');
  });
  it('samples the union of all series and includes stable log identity', () => {
    const sql = buildChartSql({ ...base, chartType: 'search', seriesList: [{ ...base.seriesList[0], where: 'severity = $p0' }, { ...base.seriesList[0], id: 'b', where: 'service = $p1' }] });
    expect(sql).toContain('(severity = $p0) OR (service = $p1)');
    expect(sql).toContain('id, service');
    expect(sql).toContain('span_id');
  });
  it('keeps ordering and limits for single series', () => {
    expect(buildChartSql({ ...base, chartType: 'pie', groupBy: ['service'] })).toMatch(/ORDER BY value DESC LIMIT 10000$/);
    expect(buildChartSql({ ...base, chartType: 'pie', groupBy: [] })).not.toContain('GROUP BY');
  });
  it('supports dashboard-bound auto buckets and explicit granularity', () => {
    expect(buildChartSql({ ...base, bucketParameter: true })).toContain('CAST($chart_bucket AS INTERVAL)');
    expect(resolveGranularity('auto', 48 * 3600000)).toBe('30m');
    expect(resolveGranularity('5m', 48 * 3600000)).toBe('5m');
  });
});
