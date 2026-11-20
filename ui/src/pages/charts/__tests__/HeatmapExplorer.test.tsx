import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { HeatmapExplorer } from '../HeatmapExplorer';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/search', async (original) => ({ ...await original<typeof import('@/api/search')>(), searchSql: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: ({ onEvents }: { onEvents?: Record<string, (event: unknown) => void> }) => <div data-testid="heatmap-chart"><button onClick={() => onEvents?.click({ value: [0, 0] })}>select cell</button><button onClick={() => onEvents?.brushEnd({ areas: [{ coordRange: [0, 2] }] })}>zoom time</button></div> }));
vi.mock('@/pages/search/SqlEditor', () => ({ SqlEditor: ({ value }: { value: string }) => <pre>{value}</pre> }));
vi.mock('../SaveChartModal', () => ({ SaveChartModal: (props: unknown) => <pre data-testid="saved-heatmap">{JSON.stringify(props)}</pre> }));
const chart = { columns: ['ts_bucket', 'y_bucket', 'bucket_min', 'bucket_max', 'value', '__bucket_ms'], rows: [], elapsedMs: 1, truncated: false };

describe('HeatmapExplorer', () => {
  beforeEach(() => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    useAuthStore.setState({ token: 'test', role: 'admin' });
    vi.mocked(searchSql).mockReset().mockResolvedValue(chart);
  });
  it('invalidates a slow response on source change and restores source drafts', async () => {
    let finish!: (value: typeof chart) => void;
    vi.mocked(searchSql).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    render(<HeatmapExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    await act(async () => finish(chart));
    expect(screen.getByTestId('heatmap-run')).not.toBeDisabled();
    expect(screen.queryByText(/已执行：/)).toBeNull();
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
  it('completes first activation under StrictMode without duplicate requests', async () => {
    render(<StrictMode><HeatmapExplorer active name="" onNameChange={vi.fn()} /></StrictMode>);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByTestId('heatmap-run')).not.toBeDisabled());
  });
  it('shows Lucene help only for Lucene conditions without running on toggle', async () => {
    render(<HeatmapExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId('heatmap-run')).not.toBeDisabled());
    expect(screen.getByTestId('chart-lucene-help')).toBeInTheDocument();
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: '使用说明' }));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.keyDown(screen.getByRole('combobox', { name: '筛选语言' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^SQL$/ }));
    expect(screen.queryByTestId('chart-lucene-help')).toBeNull();
  });

  it('runs only on activation and retains its independent draft across tabs', async () => {
    const props = { name: '', onNameChange: vi.fn() };
    const view = render(<HeatmapExplorer {...props} active={false} />);
    expect(searchSql).not.toHaveBeenCalled();
    view.rerender(<HeatmapExplorer {...props} active />);
    await waitFor(() => expect(screen.getByTestId('heatmap-run')).not.toBeDisabled());
    expect(searchSql).toHaveBeenCalledTimes(2);
    expect(vi.mocked(searchSql).mock.calls[0][0].sql).toContain('bucket_min');
    expect(screen.queryByText('添加序列')).toBeNull();
    fireEvent.change(screen.getByRole('combobox', { name: '条件' }), { target: { value: 'service:api' } });
    view.rerender(<HeatmapExplorer {...props} active={false} />);
    view.rerender(<HeatmapExplorer {...props} active />);
    expect(screen.getByRole('combobox', { name: '条件' })).toHaveValue('service:api');
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
  it('binds Lucene input and saves the executed config instead of pending edits', async () => {
    render(<HeatmapExplorer active name="Latency" onNameChange={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId('heatmap-run')).not.toBeDisabled());
    fireEvent.change(screen.getByRole('combobox', { name: '条件' }), { target: { value: 'service:api' } });
    fireEvent.click(screen.getByTestId('heatmap-run'));
    await waitFor(() => expect(screen.getByTestId('heatmap-run')).not.toBeDisabled());
    const request = vi.mocked(searchSql).mock.calls[2][0];
    expect(Object.values(request.parameters ?? {})).toContain('api');
    expect(request.sql).not.toContain('service:api');
    fireEvent.change(screen.getByRole('combobox', { name: '条件' }), { target: { value: 'service:pending' } });
    fireEvent.keyDown(screen.getByRole('button', { name: '操作' }), { key: 'Enter' });
    fireEvent.click(await screen.findByRole('menuitem', { name: '保存到仪表盘' }));
    expect(screen.getByTestId('saved-heatmap')).toHaveTextContent('duration_us');
    expect(screen.getByTestId('saved-heatmap')).not.toHaveTextContent('pending');
  });
  it('drills into executed cells and restores zoom without pending draft filters', async () => {
    vi.mocked(searchSql).mockImplementation(async (request) => {
      if (!request.sql.includes('WITH filtered')) return chart;
      const from = new Date(request.from).getTime();
      return { ...chart, columns: [...chart.columns, '__scale_min', '__scale_max'], rows: [
        [new Date(Math.floor(from / 60000) * 60000).toISOString(), 0, 0, 1, 3, 60000, 0, 6],
      ] } as typeof chart;
    });
    render(<HeatmapExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId('heatmap-run')).not.toBeDisabled());
    fireEvent.change(screen.getByRole('combobox', { name: '条件' }), { target: { value: 'service:pending' } });
    fireEvent.click(screen.getByRole('button', { name: 'select cell' }));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(3));
    const request = vi.mocked(searchSql).mock.calls[2][0];
    expect(request.sql).toContain('log10');
    expect(request.sql.match(/LIMIT 500/g)).toHaveLength(1);
    expect(Object.values(request.parameters ?? {})).not.toContain('pending');
    expect(screen.getByRole('button', { name: '清除选区' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '清除选区' }));
    expect(screen.queryByRole('button', { name: '清除选区' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'zoom time' }));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(5));
    expect(Object.values(vi.mocked(searchSql).mock.calls[3][0].parameters ?? {})).not.toContain('pending');
    fireEvent.click(screen.getByRole('button', { name: '恢复上一范围' }));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(7));
    expect(vi.mocked(searchSql).mock.calls[5][0].from).toBe(vi.mocked(searchSql).mock.calls[0][0].from);
  });
  it('hides saving for readonly users and recovers after a failed query', async () => {
    useAuthStore.setState({ role: 'readonly' });
    vi.mocked(searchSql).mockRejectedValueOnce(new Error('query failed'));
    render(<HeatmapExplorer active name="" onNameChange={vi.fn()} />);
    expect(await screen.findByRole('alert')).toHaveTextContent('query failed');
    fireEvent.click(screen.getByTestId('heatmap-run'));
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    fireEvent.keyDown(screen.getByRole('button', { name: '操作' }), { key: 'Enter' });
    expect(screen.queryByRole('menuitem', { name: '保存到仪表盘' })).toBeNull();
  });
});
