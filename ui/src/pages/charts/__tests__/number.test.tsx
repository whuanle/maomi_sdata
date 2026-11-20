import { act, render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@/i18n';
import { formatNumberValue, numberValue, numberColor } from '../numberFormat';
import { DEFAULT_NUMBER_SETTINGS as defaults } from '../numberTypes';
import { NumberChart } from '../NumberChart';
import { NumberSettingsDrawer } from '../NumberSettingsDrawer';
import { searchSql } from '@/api/search';
import { buildChartSql } from '../buildChartSql';
import { numberTrendPoints } from '../numberTrend';
import { normalizeNumberSettings } from '../numberTypes';

vi.mock('@/api/search', async (importOriginal) => ({ ...await importOriginal<typeof import('@/api/search')>(), searchSql: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="number-trend" /> }));

describe('number formatting', () => {
  it('keeps background series identities stable for duplicate aliases', () => {
    const parameters = {};
    const sql = buildChartSql({ domain: 'logs', chartType: 'time_series', seriesList: [{ id: 'a', ref: 'A', aggFunc: 'count', field: '', alias: '', where: '' }, { id: 'b', ref: 'B', aggFunc: 'count', field: '', alias: '', where: '' }], groupBy: [], granularity: 'auto', rangeMs: 3600000, bucketParameter: true, stableSeriesIds: true, parameters });
    expect(sql).toContain('__series_id');
    expect(sql).toContain('CAST($chart_bucket AS INTERVAL)');
    expect(Object.values(parameters)).toEqual(expect.arrayContaining(['a', 'b']));
  });
  it('distinguishes zero, invalid values and exact large integers', () => {
    expect(formatNumberValue(0)).toBe('0');
    for (const value of [null, undefined, '', ' ', false, {}, NaN, Infinity]) expect(numberValue(value)).toBeNull();
    expect(formatNumberValue('9007199254740993')).toBe('9007199254740993');
    expect(formatNumberValue(-1234.5, { ...defaults.format, decimals: 2, thousands: true })).toBe('-1,234.50');
    expect(formatNumberValue(1234000, { ...defaults.format, decimals: 1, compact: true })).toBe('1.2M');
  });
  it('supports all eight formats and input units', () => {
    expect(formatNumberValue(12, { ...defaults.format, output: 'currency', unit: 'CNY', decimals: 2 })).toBe('CN¥12.00');
    expect(formatNumberValue(0.25, { ...defaults.format, output: 'percentage' })).toBe('25%');
    expect(formatNumberValue(25, { ...defaults.format, output: 'percentage', unit: 'percent' })).toBe('25%');
    expect(formatNumberValue(1500, { ...defaults.format, output: 'duration', unit: 'ms', decimals: 1 })).toBe('1.5 s');
    expect(formatNumberValue(0, { ...defaults.format, output: 'duration' })).toBe('0 s');
    expect(formatNumberValue(3661, { ...defaults.format, output: 'time' })).toBe('1:01:01');
    expect(formatNumberValue(1024, { ...defaults.format, output: 'data' })).toBe('1 KiB');
    expect(formatNumberValue(1000, { ...defaults.format, output: 'data_rate', unit: 'decimal' })).toBe('1 KB/s');
    expect(formatNumberValue(42, { ...defaults.format, output: 'throughput' })).toBe('42/s');
  });
  it('uses last matching color rule and falls back for null', () => {
    const settings = { ...defaults, rules: [{ id: '1', operator: 'gte' as const, value: 10, color: 'warning' as const }, { id: '2', operator: 'gt' as const, value: 0, color: 'success' as const }] };
    expect(numberColor(42, settings)).toBe('success');
    expect(numberColor(42, { ...settings, rules: [...settings.rules].reverse() })).toBe('warning');
    expect(numberColor(null, settings)).toBe('default');
  });
});

describe('number query and trend', () => {
  const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' };
  it('aggregates p95 over the full range without grouping or buckets', () => {
    const sql = buildChartSql({ domain: 'traces', chartType: 'number', seriesList: [{ id: 'a', aggFunc: 'p95', field: 'duration_us', where: 'service = $service', alias: '' }], groupBy: ['service'], granularity: 'auto', rangeMs: 3600000 });
    expect(sql).toContain('quantile_cont("duration_us", 0.95)');
    expect(sql).toContain('ts >= $start AND ts < $end');
    expect(sql).toContain('service = $service');
    expect(sql).not.toContain('GROUP BY');
    expect(sql).not.toContain('time_bucket');
  });
  it('preserves UTC time gaps without filling missing values with zero', () => {
    const data = { columns: ['ts_bucket', 'value'], rows: [['2026-11-18 00:00:00', 2], ['2026-11-18 00:05:00', 4]] };
    expect(numberTrendPoints(data, undefined, '', true, range)).toEqual([
      [Date.parse(range.from), 2], [Date.parse(range.from) + 60000, null], [Date.parse(range.from) + 300000, 4],
    ]);
    expect(numberTrendPoints({ ...data, rows: [data.rows[0]] }, undefined, '', true, range)).toEqual([]);
  });
  it('normalizes incomplete saved configurations', () => {
    expect(normalizeNumberSettings({ format: {} as typeof defaults.format })).toMatchObject(defaults);
    expect(normalizeNumberSettings({ rules: [{ id: 'bad', operator: 'gt', value: NaN, color: 'error' }] }).rules).toEqual([]);
  });
});

describe('number display', () => {
  beforeEach(() => { vi.mocked(searchSql).mockReset(); });
  it('renders per-id overrides without fetching a disabled trend', () => {
    render(<NumberChart result={{ columns: ['__series_id', 'series', 'value'], rows: [['b', 'B', 0.25], ['a', 'A', 42]] }} items={[{ id: 'a', alias: 'A' }, { id: 'b', alias: 'B', format: { ...defaults.format, output: 'percentage' } }]} />);
    expect(screen.getByText('25%')).toBeTruthy();
    expect(screen.getByText('42')).toBeTruthy();
    expect(searchSql).not.toHaveBeenCalled();
  });
  it('isolates trend failures and binds an interval to the executed range', async () => {
    vi.mocked(searchSql).mockRejectedValue(new Error('trend failed'));
    render(<NumberChart result={{ columns: ['value'], rows: [[42]] }} settings={{ ...defaults, background: 'line' }} trendQuery={{ sql: 'SELECT value', parameters: {} }} range={{ from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' }} />);
    expect(await screen.findByText('背景趋势图加载失败，主数值仍可使用。')).toBeTruthy();
    expect(screen.getByText('42')).toBeTruthy();
    expect(vi.mocked(searchSql).mock.calls[0][0].parameters?.chart_bucket).toBe('1 minute');
  });
  it('refreshes the same absolute range only for a new execution', async () => {
    vi.mocked(searchSql).mockResolvedValue({ columns: ['ts_bucket', 'value'], rows: [], truncated: false, elapsedMs: 1 });
    const props = { result: { columns: ['value'], rows: [[42]] }, settings: { ...defaults, background: 'line' as const }, trendQuery: { sql: 'SELECT value', parameters: {} }, range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' } };
    const { rerender } = render(<NumberChart {...props} executionKey={1} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    rerender(<NumberChart {...props} settings={{ ...props.settings, color: 'error', format: { ...defaults.format, decimals: 2 } }} executionKey={1} />);
    expect(searchSql).toHaveBeenCalledTimes(1);
    rerender(<NumberChart {...props} executionKey={2} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
  });
  it('ignores stale trend responses and warns on truncation', async () => {
    type Response = Awaited<ReturnType<typeof searchSql>>;
    let oldResponse!: (value: Response) => void;
    let newResponse!: (value: Response) => void;
    vi.mocked(searchSql).mockImplementationOnce(() => new Promise((resolve) => { oldResponse = resolve; }))
      .mockImplementationOnce(() => new Promise((resolve) => { newResponse = resolve; }));
    const props = { result: { columns: ['value'], rows: [[42]] }, settings: { ...defaults, background: 'line' as const }, trendQuery: { sql: 'SELECT value', parameters: {} }, range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' } };
    const { rerender } = render(<NumberChart {...props} executionKey={1} />);
    rerender(<NumberChart {...props} executionKey={2} />);
    await act(async () => newResponse({ columns: ['ts_bucket', 'value'], rows: [], truncated: true, elapsedMs: 1 }));
    expect(screen.getByText('背景趋势已截断，不能代表完整时间范围。')).toBeTruthy();
    await act(async () => oldResponse({ columns: ['ts_bucket', 'value'], rows: [], truncated: false, elapsedMs: 1 }));
    expect(screen.getByText('背景趋势已截断，不能代表完整时间范围。')).toBeTruthy();
    expect(screen.getByText('42')).toBeTruthy();
  });
  it('applies rule order and rejects empty thresholds', () => {
    const apply = vi.fn();
    render(<NumberSettingsDrawer settings={{ ...defaults, rules: [{ id: 'a', operator: 'gte', value: 1, color: 'warning' }, { id: 'b', operator: 'gte', value: 2, color: 'error' }] }} onApply={apply} onClose={vi.fn()} />);
    fireEvent.click(screen.getAllByRole('button', { name: '下移' })[0]);
    fireEvent.change(screen.getByRole('spinbutton', { name: '阈值 1' }), { target: { value: '' } });
    expect(screen.getByRole('button', { name: /^应用$/ }).hasAttribute('disabled')).toBe(true);
    fireEvent.change(screen.getByRole('spinbutton', { name: '阈值 1' }), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: /^应用$/ }));
    expect(apply.mock.calls[0][0].rules.map((rule: { id: string }) => rule.id)).toEqual(['b', 'a']);
  });
  it('keeps drawer edits private until applied', async () => {
    const apply = vi.fn();
    render(<NumberSettingsDrawer settings={defaults} onApply={apply} onClose={vi.fn()} />);
    fireEvent.change(screen.getByRole('slider'), { target: { value: '3' } });
    expect(apply).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /^应用$/ }));
    await waitFor(() => expect(apply.mock.calls[0][0].format.decimals).toBe(3));
  });
  it('allows a series to return to inherited formatting', () => {
    const apply = vi.fn();
    render(<NumberSettingsDrawer settings={defaults} seriesOnly override={{ ...defaults.format, decimals: 3 }} onApply={apply} onClose={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^继承$/ }));
    fireEvent.click(screen.getByRole('button', { name: /^应用$/ }));
    expect(apply.mock.calls[0][1]).toBeUndefined();
  });
});
