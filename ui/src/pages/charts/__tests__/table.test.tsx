import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { ChartTable, formatTableValue } from '../ChartTable';
import { buildChartSql, type BuildChartSqlInput } from '../buildChartSql';
import { downloadCsv } from '@/pages/search/csv';
import { tableMetricNames, tableColumns } from '../tableQuery';
import { explorerResult } from '../chartResult';

vi.mock('@/pages/search/csv', () => ({ downloadCsv: vi.fn() }));
const input: BuildChartSqlInput = { domain: 'logs', chartType: 'table', seriesList: [
  { id: 'a', ref: 'A', aggFunc: 'count', field: '', alias: 'total', where: '' },
  { id: 'b', ref: 'B', aggFunc: 'count', field: '', alias: 'total', where: "severity = 'ERROR'", having: 'count(*) > 1' },
], groupBy: ['service', 'severity'], granularity: 'auto', rangeMs: 3600000, parameters: {} };

describe('aggregate table', () => {
  it('binds percentile and Top N, resolves stable sort IDs and breaks ties by dimensions', () => {
    const parameters = {};
    const series = [{ ...input.seriesList[0], aggFunc: 'p95' as const, field: 'duration_us', alias: 'p95, "latency"' }];
    const sql = buildChartSql({ ...input, domain: 'traces', groupBy: ['service', 'status'], parameters, seriesList: series, tableQuery: { topN: 10, sort: [{ columnId: series[0].id, direction: 'asc' }] } });
    expect(sql).toContain('quantile_cont("duration_us", $cf0)');
    expect(sql).toContain('ORDER BY "p95, ""latency""" ASC NULLS LAST, "service" ASC NULLS LAST, "status" ASC NULLS LAST');
    expect(sql).toContain('LIMIT $cf1');
    expect(parameters).toEqual({ cf0: 0.95, cf1: 11 });
    expect(tableColumns({ ...input, seriesList: series })[2].id).toBe(series[0].id);
    expect(() => buildChartSql({ ...input, tableQuery: { topN: 0, sort: [] } })).toThrow();
    expect(() => buildChartSql({ ...input, tableQuery: { topN: 10, sort: [{ columnId: 'unknown', direction: 'desc' }] } })).toThrow();
  });

  it('disambiguates case-insensitive and internal-looking aliases', () => {
    const names = tableMetricNames({ ...input, seriesList: [
      { ...input.seriesList[0], alias: 'SERVICE' },
      { ...input.seriesList[1], alias: '____metric' },
    ] });
    expect(names).toEqual(['SERVICE (2)', 'metric (2)']);
  });
  it('hides input columns without hiding dimensions or formula values', () => {
    const result = { columns: ['service', 'severity', 'total', 'total (2)', 'ratio'], rows: [['api', null, 2, null, null]] };
    const visible = explorerResult(result, { chartType: 'table', source: 'logs', series: input.seriesList,
      range: { from: '', to: '' }, display: 'line', autoGranularity: false, tableSettings: {},
      tableGroupBy: input.groupBy, formulas: [{ id: 'f', expression: 'A / B', alias: 'ratio' }], showInputSeries: false });
    expect(visible.columns).toEqual(['service', 'severity', 'ratio']);
    expect(visible.rows).toEqual([['api', null, null]]);
  });
  it('aligns complete group tuples and produces unique wide metrics without time buckets', () => {
    const sql = buildChartSql(input);
    expect(sql).not.toContain('time_bucket');
    expect(sql).toContain('GROUP BY "service", "severity"');
    expect(sql).toContain('AS "total"');
    expect(sql).toContain('AS "total (2)"');
    expect(sql).toContain('HAVING (count(*) > 1)');
    expect(sql).toContain('LIMIT 501');
    expect(sql).toContain('THEN value END');
  });
  it('supports formulas, quoted labels and validated query-level ordering', () => {
    const sql = buildChartSql({ ...input, formulas: [{ id: 'f', expression: 'A / B', alias: 'ratio' }], tableOrderBy: 'A DESC, service ASC' });
    expect(sql).toContain('AS "ratio"');
    expect(sql).toContain('ORDER BY "total" DESC NULLS LAST, "service" ASC NULLS LAST');
    expect(() => buildChartSql({ ...input, tableOrderBy: 'service; DROP TABLE logs' })).toThrow();
    expect(() => buildChartSql({ ...input, tableOrderBy: 'unknown' })).toThrow();
  });
  it('supports a scalar default table', () => {
    const sql = buildChartSql({ ...input, seriesList: input.seriesList.slice(0, 1), groupBy: [] });
    expect(sql).not.toContain('GROUP BY');
    expect(sql).toContain('v0');
  });
  it('reads dotted columns by position, sorts nulls last and exports sorted rows', () => {
    const result = { columns: ['service.name', 'count'], rows: [['b', 2], ['a', 4], ['c', null]] };
    render(<ChartTable result={result} />);
    expect(screen.getByText('b')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /count/ }));
    const rows = screen.getAllByRole('row');
    expect(within(rows[1]).getByText('a')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '下载 CSV' }));
    expect(downloadCsv).toHaveBeenCalledWith(result.columns, [['a', 4], ['b', 2], ['c', null]], 'chart-results.csv');
  });
  it('keeps null empty and formats numbers', () => {
    expect(formatTableValue(null)).toBe('—');
    expect(formatTableValue(1024, { format: 'bytes' })).toBe('1 KiB');
    expect(formatTableValue(12.345, { decimals: 1, format: 'percent' })).toBe('12.3%');
  });
});
