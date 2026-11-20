import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { PieExplorer } from '../PieExplorer';
import { pieData } from '../pieDisplay';
import { buildChartOption } from '../chartOption';
import { DEFAULT_VALUE_FORMAT } from '../barFormat';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';
import { ChartWorkspaceContext } from '../ChartWorkspaceContext';
import type { ChartEditorDefinition } from '@/api/chartTypes';
import type { PanelConfig } from '@/api/types';
import { queryChartMetrics, queryChartMetricDetails } from '@/api/chartMetrics';
vi.mock('@/api/chartMetrics', () => ({ queryChartMetrics: vi.fn(), queryChartMetricDetails: vi.fn() }));

vi.mock('@/api/search', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/api/search')>()), searchSql: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: ({ option }: { option: unknown }) => <div data-testid="pie-option">{JSON.stringify(option)}</div> }));
vi.mock('@/pages/search/SqlEditor', () => ({ SqlEditor: ({ value }: { value: string }) => <pre>{value}</pre> }));
const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' };
const result = { columns: ['service', 'value'], rows: [['api', 7], ['db', 3]], elapsedMs: 1, truncated: false };

beforeEach(() => {
  HTMLElement.prototype.scrollIntoView = vi.fn();
  vi.mocked(searchSql).mockReset().mockResolvedValue(result);
  useAuthStore.setState({ role: 'admin' });
});

describe('Pie data', () => {
  it('keeps full identities, stable colors and largest categories', () => {
    const source = { columns: ['a', 'b', 'value'], rows: [['x / y', 'z', 7], ['x', 'y / z', 3], ['x / y', 'z', 2]] };
    const all = pieData(source, 'count');
    expect(all.data.map((item) => item.value)).toEqual([9, 3]);
    expect(new Set(all.data.map((item) => item.name)).size).toBe(2);
    const limited = pieData(source, 'count', { seriesLimit: 1, format: DEFAULT_VALUE_FORMAT });
    expect(limited.limited).toBe(true);
    expect(limited.data).toEqual(all.data.slice(0, 1));
    expect(pieData({ ...source, rows: [...source.rows].reverse() }, 'count').data).toEqual(all.data);
  });
  it('rejects negative proportions and avoids all-zero or non-finite pies', () => {
    for (const rows of [[[0]], [[null], [''], [Infinity]], [[5], [-1]]]) {
      expect(buildChartOption({ columns: ['value'], rows }, 'pie', range, [], true, 'line', 'gray')).toBeNull();
    }
    expect(buildChartOption({ columns: ['value'], rows: [[5]] }, 'pie', range, [], true, 'line', 'gray')?.series).toMatchObject([{ type: 'pie', stillShowZeroSum: false }]);
  });
});

describe('PieExplorer', () => {
  it.each(['logs', 'traces', 'metrics', 'sessions'] as const)('applies and reopens the executed %s definition without losing legend settings', async (source) => {
    const definition: ChartEditorDefinition = { version: 1, chartType: 'pie', mode: 'builder', source, series: [{ id: 'pie', aggFunc: 'count', field: '', where: '', alias: '', ...(source === 'metrics' ? { metricName: 'cpu' } : {}) }], syntax: 'sql', groupBy: [source === 'metrics' ? 'labels["host"]' : source === 'sessions' ? 'domain' : 'service'], formulas: [], granularity: 'auto' };
    const onApply = vi.fn();
    const initial = { id: 'existing', type: 'pie', title: 'Existing', showLegend: false, layout: { x: 0, y: 0, w: 6, h: 4 } } as PanelConfig;
    const view = render(<ChartWorkspaceContext.Provider value={{ definition, initial, onApply, range }}><PieExplorer active name="Saved" onNameChange={vi.fn()} /></ChartWorkspaceContext.Provider>);
    fireEvent.click(screen.getByTestId('pie-run'));
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    fireEvent.change(screen.getByRole('textbox', { name: '分组' }), { target: { value: 'unexecuted' } });
    fireEvent.click(screen.getByRole('button', { name: '应用到画布' }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '应用到画布' }));
    expect(onApply).toHaveBeenCalledOnce();
    const panel = onApply.mock.calls[0][0] as PanelConfig;
    expect(panel).toMatchObject({ id: 'existing', showLegend: false, explorer: { editor: definition } });
    view.unmount();
    render(<ChartWorkspaceContext.Provider value={{ initial: panel, definition: panel.explorer!.editor, onApply, range }}><PieExplorer active name="Saved" onNameChange={vi.fn()} /></ChartWorkspaceContext.Provider>);
    expect(screen.getByRole('textbox', { name: '分组' })).toHaveValue(definition.groupBy.join(', '));
  });
  it('runs traces and sessions, and restores the logs grouping after source changes', async () => {
    render(<PieExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    fireEvent.change(screen.getByRole('textbox', { name: '分组' }), { target: { value: 'severity' } });
    for (const [label, source, group] of [['链路', 'traces', 'service'], ['客户端会话', 'sessions', 'domain']] as const) {
      fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
      fireEvent.click(await screen.findByRole('option', { name: label }));
      fireEvent.change(screen.getByRole('textbox', { name: '分组' }), { target: { value: group } });
      fireEvent.click(screen.getByTestId('pie-run'));
      await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
      expect(vi.mocked(searchSql).mock.calls[vi.mocked(searchSql).mock.calls.length - 2][0].sql).toContain('{{' + source + '}}');
      expect(screen.getByTestId('pie-option')).toBeInTheDocument();
    }
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: '日志' }));
    expect(screen.getByRole('textbox', { name: '分组' })).toHaveValue('severity');
  });
  it('selects a metric, groups its labels and retains bound parameters in SQL mode', async () => {
    vi.mocked(queryChartMetrics).mockResolvedValue({ items: [{ name: "cpu'usage", kinds: ['gauge'] }], truncated: false });
    vi.mocked(queryChartMetricDetails).mockResolvedValue({ labels: [], truncated: false });
    render(<PieExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    fireEvent.click(screen.getByTestId('metric-picker-0'));
    fireEvent.click(await screen.findByRole('option', { name: /cpu'usage/ }));
    fireEvent.change(screen.getByRole('textbox', { name: '分组' }), { target: { value: 'labels["host"]' } });
    fireEvent.click(screen.getByTestId('pie-run'));
    expect(screen.queryByRole('alert')?.textContent ?? '').toBe('');
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    const request = vi.mocked(searchSql).mock.calls[2][0];
    expect(request.sql).toContain('json_extract_string');
    expect(Object.values(request.parameters!)).toContain("cpu'usage");
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    fireEvent.click(screen.getByTestId('pie-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(5));
    expect(vi.mocked(searchSql).mock.calls[4][0].parameters).toEqual(request.parameters);
  });
  it('requires a metric before issuing a new metrics query', async () => {
    render(<PieExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    expect(screen.getByTestId('metric-picker-0')).toBeInTheDocument();
    fireEvent.click(screen.getByTestId('pie-run'));
    expect(screen.getByRole('alert')).toHaveTextContent('请选择要统计的指标');
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
  it('invalidates a slow response on source change and restores source drafts', async () => {
    let finish!: (value: typeof result) => void;
    vi.mocked(searchSql).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    render(<PieExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    await act(async () => finish(result));
    expect(screen.getByTestId('pie-run')).not.toBeDisabled();
    expect(screen.queryByText(/已执行：/)).toBeNull();
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
  it('completes first activation under StrictMode without duplicate requests', async () => {
    render(<StrictMode><PieExplorer active name="" onNameChange={vi.fn()} /></StrictMode>);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
  });
  it('binds Lucene values and retains parameters when switching to SQL', async () => {
    render(<PieExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    expect(screen.getByTestId('chart-lucene-help')).toBeInTheDocument();
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: '使用说明' }));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.change(screen.getByRole('combobox', { name: '条件' }), { target: { value: 'service:api' } });
    fireEvent.click(screen.getByTestId('pie-run'));
    expect(screen.queryByRole('alert')?.textContent ?? '').toBe('');
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(4));
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    const builder = vi.mocked(searchSql).mock.calls[2][0];
    expect(builder.sql).not.toContain("'api'");
    expect(Object.values(builder.parameters!)).toContain('api');
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    expect(screen.queryByTestId('chart-lucene-help')).toBeNull();
    fireEvent.click(screen.getByTestId('pie-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(5));
    expect(vi.mocked(searchSql).mock.calls[4][0].parameters).toEqual(builder.parameters);
  });
  it('runs ungrouped single aggregation and applies settings only on confirmation', async () => {
    render(<PieExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    expect(vi.mocked(searchSql).mock.calls[0][0].sql).not.toContain('time_bucket');
    expect(screen.queryByRole('button', { name: '添加序列' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '显示设置' }));
    let dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: '分类数量上限' }), { target: { value: '1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'close' }));
    expect(screen.getByTestId('pie-option')).toHaveTextContent('db');
    fireEvent.click(screen.getByRole('button', { name: '显示设置' }));
    dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: '分类数量上限' }), { target: { value: '-1' } });
    expect(within(dialog).getByRole('button', { name: '应用' })).toBeDisabled();
    fireEvent.change(within(dialog).getByRole('textbox', { name: '分类数量上限' }), { target: { value: '1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '应用' }));
    expect(screen.getByTestId('pie-option')).not.toHaveTextContent('db');
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
  it('preserves drafts across tabs and hides save for readonly', async () => {
    useAuthStore.setState({ role: 'readonly' });
    const props = { active: true, name: '', onNameChange: vi.fn() };
    const view = render(<PieExplorer {...props} />);
    await waitFor(() => expect(screen.getByTestId('pie-run')).not.toBeDisabled());
    fireEvent.change(screen.getByRole('textbox', { name: '分组' }), { target: { value: 'service' } });
    view.rerender(<PieExplorer {...props} active={false} />);
    expect(screen.queryByTestId('pie-explorer')).toBeNull();
    view.rerender(<PieExplorer {...props} />);
    expect(screen.getByRole('textbox', { name: '分组' })).toHaveValue('service');
    expect(screen.queryByRole('button', { name: '保存到仪表盘' })).toBeNull();
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
});
