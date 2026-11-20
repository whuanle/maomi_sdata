import { describe, expect, it } from 'vitest';
import { buildHeatmapSql, buildDistributionOption, distributionGrid, heatmapZoomRange, buildHeatmapSampleSql, DEFAULT_HEATMAP } from '../heatmap';
import { buildChartSql } from '../buildChartSql';
import type { SearchSqlParameters } from '@/api/types';

const legacy = { field: 'duration_us' as const, bins: 40 };
const labels = { low: 'Low', high: 'High', count: 'Count', duration: 'Duration', value: 'Value', noData: 'No data' };
const columns = ['ts_bucket', 'y_bucket', 'bucket_min', 'bucket_max', 'value', '__bucket_ms'];
const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T00:05:00Z' };
const result = { columns, rows: [['2026-11-18T00:01:00Z', 0, 0, 1000, 3, 60000], ['2026-11-18T00:03:00Z', 39, 39000, 40000, 7, 60000]] };

describe('distribution heatmap', () => {
  it('aggregates full input in DuckDB and binds bucket count', () => {
    const parameters: SearchSqlParameters = {};
    const sql = buildChartSql({ domain: 'traces', chartType: 'heatmap', heatmap: DEFAULT_HEATMAP, seriesList: [{ id: 'A', aggFunc: 'count', field: '', alias: '', where: 'service = $service' }], groupBy: [], granularity: 'auto', rangeMs: 3600000, bucketParameter: true, parameters });
    expect(sql).toContain('FROM {{traces}}');
    expect(sql).toContain('log10(1 + abs(sample))');
    expect(sql).toContain('__scale_min');
    expect(sql).toContain('LIMIT 10001');
    expect(sql).toContain('service = $service');
    expect(sql).toContain('count(*) AS value');
    expect(sql).toContain('isfinite');
    expect(sql).toContain('duration_us >= 0');
    expect(sql).not.toContain('SAMPLE');
    expect(Object.values(parameters)).toContain(60);
    expect(Object.values(parameters)).toContain(159);
    expect(buildHeatmapSql('traces', legacy, '', 'CAST($chart_bucket AS INTERVAL)', {})).toContain('CAST($chart_bucket AS INTERVAL)');
  });
  it('rejects unsupported fields and unbounded grids', () => {
    expect(() => buildHeatmapSql('logs', DEFAULT_HEATMAP, '', "INTERVAL '1 minute'", {})).toThrow();
    expect(() => buildHeatmapSql('traces', { ...DEFAULT_HEATMAP, bins: 10000 }, '', "INTERVAL '1 minute'", {})).toThrow();
    expect(() => buildHeatmapSql('traces', { ...DEFAULT_HEATMAP, field: 'value' }, '', "INTERVAL '1 minute'", {})).toThrow();
  });
  it('preserves missing times, numeric bins and count colors', () => {
    const option = buildDistributionOption(result, range, legacy, true, labels)!;
    expect(option.xAxis).toMatchObject({ data: expect.any(Array) });
    expect((option.xAxis as { data: unknown[] }).data).toHaveLength(5);
    expect((option.yAxis as { data: unknown[] }).data).toHaveLength(40);
    expect(option.series).toMatchObject([{ type: 'heatmap', data: [{ value: [1, 0, 3, 3] }, { value: [3, 39, 7, 7] }] }]);
    expect(option.visualMap).toMatchObject({ show: true, max: 7, text: ['High', 'Low'] });
    expect(buildDistributionOption(result, range, legacy, false, labels)?.visualMap).toMatchObject({ show: false });
  });
  it('shows duration range, time and count in rich text tooltip', () => {
    const option = buildDistributionOption(result, range, legacy, true, labels)!;
    const formatter = (option.tooltip as { formatter: (value: unknown) => string }).formatter;
    expect(formatter({ value: [1, 0, 3] })).toContain('Count: 3');
    expect(formatter({ value: [1, 0, 3] })).toContain('1.0 ms');
  });
  it('reconstructs sparse log boundaries and preserves raw counts', () => {
    const logResult = { columns: [...columns, '__scale_min', '__scale_max'], rows: [
      ['2026-11-18T00:01:00Z', 0, 0, Math.pow(10, 0.1) - 1, 1000, 60000, 0, 6],
      ['2026-11-18T00:03:00Z', 59, Math.pow(10, 5.9) - 1, 999999, 1, 60000, 0, 6],
    ] };
    const grid = distributionGrid(logResult, range, DEFAULT_HEATMAP)!;
    expect(grid.bounds).toHaveLength(60);
    expect(grid.bounds[30][0]).toBeCloseTo(999);
    expect(grid.cells.reduce((sum, cell) => sum + cell.count, 0)).toBe(1001);
    const option = buildDistributionOption(logResult, range, DEFAULT_HEATMAP, true, labels)!;
    expect(option.series).toMatchObject([{ data: [{ value: [1, 0, Math.log1p(1000), 1000] }, { value: [3, 59, Math.log1p(1), 1] }] }]);
    expect((option.tooltip as { formatter: (value: unknown) => string }).formatter({ value: [1, 0] })).toContain('Count: 1,000');
  });
  it('maps category selections to clipped time ranges', () => {
    const grid = distributionGrid(result, range, legacy)!;
    expect(heatmapZoomRange(grid, range, 1, 2)).toEqual({ from: '2026-11-18T00:01:00.000Z', to: '2026-11-18T00:03:00.000Z' });
    expect(heatmapZoomRange(grid, range, 0, 4)).toBeNull();
    expect(heatmapZoomRange(grid, range, NaN, 4)).toBeNull();
  });
  it('filters the full matching input before applying the sample limit', () => {
    const parameters: SearchSqlParameters = {};
    const cell = distributionGrid(result, range, legacy)!.cells[0];
    const sql = buildHeatmapSampleSql('SELECT * FROM {{traces}} WHERE service = $service\nORDER BY ts DESC\nLIMIT 500', legacy, cell, parameters);
    expect(sql.match(/LIMIT 500/g)).toHaveLength(1);
    expect(sql).toContain('floor(');
    expect(sql).toContain('isfinite(CAST("duration_us" AS DOUBLE))');
    expect(sql).not.toContain(cell.from);
    expect(Object.values(parameters)).toContain(cell.from);
  });
  it('handles empty, nonfinite and legacy results without false data', () => {
    expect(buildDistributionOption({ columns, rows: [] }, range, DEFAULT_HEATMAP, true, labels)).toMatchObject({ title: { text: 'No data' }, series: [] });
    expect(buildDistributionOption({ columns, rows: [['2026-11-18', 0, null, 2, Infinity, 60000]] }, range, DEFAULT_HEATMAP, true, labels)).toMatchObject({ series: [] });
    expect(buildDistributionOption({ columns: ['ts_bucket', 'service', 'value'], rows: [] }, range, DEFAULT_HEATMAP, true, labels)).toBeNull();
  });
});
