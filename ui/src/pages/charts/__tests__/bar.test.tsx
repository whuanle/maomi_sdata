import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { StrictMode } from 'react';
import { BarExplorer } from '../BarExplorer';
import { buildChartSql } from '../buildChartSql';
import { barData, buildBarOption } from '../barDisplay';
import { DEFAULT_VALUE_FORMAT, formatBarAxisValue, formatBarValue } from '../barFormat';
import { bindBarWhere } from '../barWhere';
import { barColumn } from '../barColumn';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';
import type { BuildChartSqlInput } from '../buildChartSql';

vi.mock('@/api/search', async (original) => ({ ...(await original<typeof import('@/api/search')>()), searchSql: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: ({ option }: { option: unknown }) => <div data-testid="bar-option">{JSON.stringify(option)}</div> }));
vi.mock('@/pages/search/SqlEditor', () => ({ SqlEditor: ({ value, onChange }: { value: string; onChange?: (value: string) => void }) => <textarea aria-label="sql" value={value} readOnly={!onChange} onChange={(event) => onChange?.(event.target.value)} /> }));
const result = { columns: ['service', 'value'], rows: [['api', 7], ['db', -3]], elapsedMs: 1, truncated: false };
const input = (): BuildChartSqlInput => ({ domain: 'logs', chartType: 'bar', seriesList: [{ id: 'bar', aggFunc: 'count', field: '', alias: "drop table", where: '' }], groupBy: ['service'], granularity: 'auto', rangeMs: 3600000, parameters: {} });

beforeEach(() => {
  vi.mocked(searchSql).mockReset().mockResolvedValue(result);
  useAuthStore.setState({ role: 'admin' });
  HTMLElement.prototype.scrollIntoView = vi.fn();
});

describe('Bar query and display', () => {
  it('formats axis ticks compactly without losing tooltip precision', () => {
    expect(formatBarAxisValue(4000)).toBe('4K');
    expect(formatBarAxisValue(12000)).toBe('12K');
    expect(formatBarAxisValue(-2500000)).toBe('-2.5M');
    expect(formatBarAxisValue(0)).toBe('0');
    expect(formatBarAxisValue(0.25, { ...DEFAULT_VALUE_FORMAT, output: 'percentage' })).toBe('25%');
    expect(formatBarAxisValue(0.001, { ...DEFAULT_VALUE_FORMAT, output: 'duration' })).toBe('1.00 ms');
    expect(formatBarValue(23456.78)).toBe('23,456.78');
  });
  it('uses category bandwidth for a single bar and keeps category labels horizontal', () => {
    const option = buildBarOption({ columns: ['value'], rows: [[23456]] }, 'count()', undefined, 'gray');
    expect(option?.series).toMatchObject([{ barWidth: '80%', data: [23456] }]);
    expect(option?.xAxis).toMatchObject({ type: 'category', boundaryGap: true, axisLabel: { rotate: 0, overflow: 'truncate', hideOverlap: true } });
    expect(option?.legend).toMatchObject({ show: false });
    expect(option?.dataZoom).toBeUndefined();
    const many = buildBarOption({ columns: ['category', 'value'], rows: Array.from({ length: 40 }, (_, i) => ['category ' + i, i]) }, 'count()', undefined, 'gray');
    expect(many?.dataZoom).toMatchObject([{ type: 'slider', endValue: 29 }]);
  });
  it('compiles Column arithmetic and JSON paths with bound constants', () => {
    const parameters = {};
    const sql = barColumn('(duration_us) / 1000', 'traces', parameters);
    expect(sql).toContain('TRY_CAST("duration_us" AS DOUBLE)');
    expect(sql).toContain('NULLIF(');
    expect(Object.values(parameters)).toContain(1000);
    const jsonParameters = {};
    expect(barColumn('attributes["latency"] / 1000', 'logs', jsonParameters)).toContain('json_extract_string');
    expect(Object.values(jsonParameters)).toContain('$."latency"');
    for (const expression of ['', 'unknown', 'sum(duration_us)', 'duration_us; DROP', 'duration_us -- comment']) {
      expect(() => barColumn(expression, 'traces', {})).toThrow();
    }
    const state = input();
    state.domain = 'traces';
    state.seriesList = [{ ...state.seriesList[0], aggFunc: 'sum', field: 'duration_us / 1000' }];
    expect(buildChartSql(state)).toContain('sum(');
    expect(Object.values(state.parameters!)).toContain(1000);
  });
  it('binds SQL literals and rejects statement boundaries', () => {
    const parameters = {};
    const sql = bindBarWhere("severity = 'ERROR' AND duration_us >= 1e3 AND service = 'it''s api'", parameters);
    expect(sql).not.toContain("'ERROR'");
    expect(Object.values(parameters)).toEqual(['ERROR', 1000, "it's api"]);
    expect(() => bindBarWhere("service = 'api'; SELECT 1", {})).toThrow();
    expect(() => bindBarWhere("service = 'api' -- comment", {})).toThrow();
    expect(() => bindBarWhere("service = 'unclosed", {})).toThrow();
  });
  it('selects largest categories before display sorting with bound limits and no buckets', () => {
    const state = { ...input(), orderBy: 'service ASC', seriesLimit: 2 };
    const sql = buildChartSql(state);
    expect(sql).toContain('GROUP BY "service"');
    expect(sql).toContain('selected AS (SELECT * FROM aggregated ORDER BY value DESC NULLS LAST');
    expect(sql).toContain('ORDER BY "service" ASC NULLS LAST');
    expect(Object.values(state.parameters!)).toContain(2);
    expect(sql).not.toMatch(/time_bucket|ts_bucket|chart_bucket|drop table/);
  });
  it('rejects extra series, unknown fields and injected ordering', () => {
    const state = input();
    expect(() => buildChartSql({ ...state, seriesList: [...state.seriesList, ...state.seriesList] })).toThrow();
    expect(() => buildChartSql({ ...state, groupBy: ['service;DROP'] })).toThrow();
    expect(() => buildChartSql({ ...state, orderBy: 'value DESC;DROP' })).toThrow();
    expect(() => buildChartSql({ ...state, seriesLimit: -1 })).toThrow();
  });
  it('retains negatives, nulls and distinct tuple labels on a categorical axis', () => {
    const rows = { columns: ['ts_bucket', 'other', 'value'], rows: [['a / b', 'c', -4], ['a', 'b / c', 0], [null, '', null]] };
    const data = barData(rows, 'Total');
    expect(new Set(data.data.map((item) => item.name)).size).toBe(3);
    expect(data.data.map((item) => item.value)).toEqual([-4, 0, null]);
    const option = buildBarOption(rows, 'Total', { format: DEFAULT_VALUE_FORMAT }, 'gray');
    expect(option?.xAxis).toMatchObject({ type: 'category' });
    expect(option?.series).toMatchObject([{ type: 'bar', data: [-4, 0, null] }]);
    expect(buildBarOption({ columns: ['value'], rows: [[null]] }, 'Total', undefined, 'gray')).toBeNull();
  });
});

describe('BarExplorer', () => {
  it('shows source-aware Lucene help and hides it for SQL without running on toggle', async () => {
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByTestId('bar-option');
    expect(screen.getByTestId('chart-lucene-help')).toHaveTextContent('service:api*');
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: '使用说明' }));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.keyDown(screen.getByRole('combobox', { name: '查询语言' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^SQL$/ }));
    expect(screen.queryByTestId('chart-lucene-help')).toBeNull();
    fireEvent.keyDown(screen.getByRole('combobox', { name: '查询语言' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^Lucene$/ }));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    expect(screen.getByTestId('chart-lucene-help')).toHaveTextContent('name:cpu*');
    expect(screen.getByTestId('chart-lucene-help')).not.toHaveTextContent('service:api*');
  });

  it('filters samples using the executed window rather than unrun drafts and can remove the filter', async () => {
    const sample = { ...result, columns: ['ts', 'service', 'severity', 'body'], rows: [['2026-11-19T00:00:00Z', 'api', 'ERROR', 'boom']] };
    vi.mocked(searchSql).mockImplementation(async ({ sql }) => sql.includes('ORDER BY ts DESC') ? sample : result);
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByTestId('bar-option');
    const original = vi.mocked(searchSql).mock.calls[0][0];
    fireEvent.change(screen.getByTestId('bar-where'), { target: { value: 'service:unrun' } });
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByRole('button', { name: '行明细' }));
    fireEvent.click(screen.getAllByRole('button', { name: '按此值过滤' })[0]);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(screen.getByTestId('bar-run')).not.toBeDisabled());
    const filtered = vi.mocked(searchSql).mock.calls[2][0];
    expect(filtered.from).toBe(original.from);
    expect(filtered.to).toBe(original.to);
    expect(Object.values(filtered.parameters!)).not.toContain('unrun');
    expect(screen.getByTestId('bar-where')).toHaveValue('');
    const remove = screen.getByRole('button', { name: /^删除 / });
    fireEvent.click(remove);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(6));
    expect(screen.queryByRole('button', { name: /^删除 / })).toBeNull();
  });
  it('keeps successful samples usable when the aggregate query fails', async () => {
    vi.mocked(searchSql).mockImplementation(async ({ sql }) => {
      if (sql.includes('ORDER BY ts DESC')) return { ...result, columns: ['service', 'body'], rows: [['api', 'sample body']] };
      throw new Error('aggregate failed');
    });
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByText('aggregate failed');
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    expect(screen.getByTitle('sample body')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '保存到仪表盘' })).toBeDisabled();
  });

  it('shows editable SQL Column for Sum, hides it for Count and retains its draft', async () => {
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByTestId('bar-option');
    expect(screen.queryByTestId('bar-column')).toBeNull();
    const aggregation = screen.getByRole('combobox', { name: 'Bar 聚合' });
    const choose = async (name: string) => {
      fireEvent.keyDown(aggregation, { key: 'ArrowDown' });
      fireEvent.click(await screen.findByRole('option', { name }));
    };
    await choose('求和');
    const column = screen.getByTestId('bar-column');
    expect(column).toHaveAttribute('placeholder', 'SQL Column');
    fireEvent.change(column, { target: { value: 'attributes["latency"] / 1000' } });
    fireEvent.click(screen.getByTestId('bar-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    expect(Object.values(vi.mocked(searchSql).mock.calls[2][0].parameters!)).toContain(1000);
    await choose('事件计数');
    expect(screen.queryByTestId('bar-column')).toBeNull();
    await choose('求和');
    expect(screen.getByTestId('bar-column')).toHaveValue('attributes["latency"] / 1000');
  });
  it('switches WHERE syntax with independent drafts and binds SQL values', async () => {
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByTestId('bar-option');
    const where = screen.getByTestId('bar-where');
    fireEvent.change(where, { target: { value: 'service:api' } });
    const changeSyntax = async (name: string) => {
      fireEvent.keyDown(screen.getByRole('combobox', { name: '查询语言' }), { key: 'ArrowDown' });
      fireEvent.click(await screen.findByRole('option', { name }));
    };
    await changeSyntax('SQL');
    expect(where).toHaveValue('');
    fireEvent.focus(where);
    fireEvent.change(where, { target: { value: 'sev' } });
    expect(await screen.findByRole('option', { name: /severity/ })).toBeInTheDocument();
    fireEvent.keyDown(where, { key: 'Enter' });
    expect(where).toHaveValue('severity ');
    fireEvent.change(where, { target: { value: "severity = 'ERROR'" } });
    fireEvent.keyDown(where, { key: 'Escape' });
    fireEvent.click(screen.getByTestId('bar-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    const request = vi.mocked(searchSql).mock.calls[2][0];
    expect(request.sql).not.toContain("'ERROR'");
    expect(Object.values(request.parameters!)).toContain('ERROR');
    await changeSyntax('Lucene');
    expect(where).toHaveValue('service:api');
    await changeSyntax('SQL');
    expect(where).toHaveValue("severity = 'ERROR'");
  });
  it('suggests Lucene fields and values without running the chart on acceptance', async () => {
    vi.mocked(searchSql).mockImplementation(async (request) => request.sql.includes('SELECT v, count(*) AS c')
      ? { ...result, columns: ['v', 'c'], rows: [['ERROR', 8]] } : result);
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByTestId('bar-option');
    const where = screen.getByTestId('bar-where');
    fireEvent.focus(where);
    fireEvent.change(where, { target: { value: 'sev' } });
    expect(await screen.findByRole('option', { name: /severity/ })).toBeInTheDocument();
    fireEvent.keyDown(where, { key: 'Enter' });
    expect(where).toHaveValue('severity:');
    expect(await screen.findByRole('option', { name: /ERROR/ })).toBeInTheDocument();
    fireEvent.keyDown(where, { key: 'Enter' });
    expect(where).toHaveValue('severity:ERROR ');
    expect(vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('WITH aggregated'))).toHaveLength(1);
  });
  it('finishes its initial query under Strict Mode without duplicate requests', async () => {
    render(<StrictMode><BarExplorer active name="" onNameChange={vi.fn()} /></StrictMode>);
    await screen.findByTestId('bar-option');
    expect(searchSql).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId('bar-run')).not.toBeDisabled();
  });
  it('runs a single aggregation and binds filters, then preserves SQL parameters', async () => {
    render(<BarExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId('bar-run')).not.toBeDisabled());
    expect(screen.queryByRole('button', { name: '添加序列' })).toBeNull();
    expect(screen.queryByText('粒度')).toBeNull();
    fireEvent.change(screen.getByTestId('bar-where'), { target: { value: 'service:api' } });
    fireEvent.click(screen.getByTestId('bar-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(screen.getByTestId('bar-run')).not.toBeDisabled());
    const request = vi.mocked(searchSql).mock.calls[2][0];
    expect(request.sql).not.toContain("'api'");
    expect(Object.values(request.parameters!)).toContain('api');
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    fireEvent.click(screen.getByTestId('bar-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(5));
    expect(vi.mocked(searchSql).mock.calls[4][0].parameters).toEqual(request.parameters);
  });
  it('applies Top N only after Run and retains a readonly draft across activation', async () => {
    useAuthStore.setState({ role: 'readonly' });
    const props = { active: true, name: '', onNameChange: vi.fn() };
    const view = render(<BarExplorer {...props} />);
    await waitFor(() => expect(screen.getByTestId('bar-run')).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '显示设置' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: '分类数量上限' }), { target: { value: '1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '应用' }));
    expect(searchSql).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId('bar-option')).toHaveTextContent('db');
    fireEvent.click(screen.getByTestId('bar-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    expect(Object.values(vi.mocked(searchSql).mock.calls[2][0].parameters!)).toContain(1);
    fireEvent.change(screen.getByRole('textbox', { name: '别名' }), { target: { value: 'My count' } });
    view.rerender(<BarExplorer {...props} active={false} />);
    view.rerender(<BarExplorer {...props} />);
    expect(screen.getByRole('textbox', { name: '别名' })).toHaveValue('My count');
    expect(screen.queryByRole('button', { name: '保存到仪表盘' })).toBeNull();
  });
});
