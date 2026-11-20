import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@/i18n';
import { TraceDetailDrawer } from '../TraceDetailDrawer';
import { loadTraceWindow, loadTraceLogs, loadSpan } from '@/api/traceSearch';
import { searchSql } from '@/api/search';

vi.mock('@/api/search', async (original) => ({ ...(await original<typeof import('@/api/search')>()), searchSql: vi.fn() }));

vi.mock('@/api/traceSearch', () => ({ loadTraceWindow: vi.fn(), loadTraceLogs: vi.fn(), loadSpan: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="chart" /> }));
vi.mock('../LogDetailDrawer', () => ({
  LogDetailDrawer: () => null,
  OverviewTab: ({ record }: { record: Record<string, unknown> }) => <div data-testid="selected-span">{String(record.span_id)}</div>,
  ValuesTab: () => null,
}));
const root = { trace_id: 'trace', span_id: 'root', parent_span_id: null, ts: '2026-11-17T10:00:00Z', duration_us: 10000, service: 'gateway', name: 'root operation', status: 'ok', kind: 'server', events: '[]', attributes: '{}', resource: '{}' };
const child = { ...root, span_id: 'child', parent_span_id: 'root', name: 'child operation', service: 'db', duration_us: 500 };
const props = { row: root, from: '2026-11-17T09:00:00Z', to: '2026-11-17T11:00:00Z', onClose: vi.fn(), onFilterByValue: vi.fn(), onFilterOnlyValue: vi.fn() };

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(loadSpan).mockResolvedValue(null);
  vi.mocked(loadTraceWindow).mockResolvedValue({ rows: [root, child], truncated: false });
  vi.mocked(loadTraceLogs).mockResolvedValue({ rows: [], truncated: false });
});

describe('TraceDetailDrawer', () => {
  it('上下文以选中 span 查询附近 traces，不隐式限制同 trace', async () => {
    vi.mocked(searchSql).mockResolvedValue({ columns: ['ts', 'service', 'name', 'duration_us', 'status', 'trace_id', 'span_id'], rows: [[root.ts, 'other-service', 'nearby span', 500, 'ok', 'other-trace', 'nearby']], elapsedMs: 1, truncated: false });
    render(<TraceDetailDrawer {...props} />);
    await screen.findByRole('button', { name: 'db · child operation' });
    fireEvent.click(screen.getByRole('button', { name: 'db · child operation' }));
    fireEvent.mouseDown(screen.getByRole('tab', { name: '上下文' }), { button: 0, ctrlKey: false });
    await screen.findByText('nearby span');
    const request = vi.mocked(searchSql).mock.calls[0][0];
    expect(request.sql).toContain('FROM {{traces}}');
    expect(request.sql).not.toContain('trace_id =');
    expect(request.from).toBe('2026-11-17T09:59:30.000000Z');
    fireEvent.click(screen.getByText('nearby span'));
    await waitFor(() => expect(screen.getByTestId('selected-span').textContent).toBe('nearby'));
    await waitFor(() => expect(loadSpan).toHaveBeenCalled());
    expect(searchSql).toHaveBeenCalledTimes(1);
  });

  it('同一份链路数据选择 span 后原地切换属性，折叠子树并复位', async () => {
    render(<TraceDetailDrawer {...props} />);
    await screen.findByRole('button', { name: 'db · child operation' });
    expect(screen.getByTestId('selected-span').textContent).toBe('root');
    fireEvent.click(screen.getByRole('button', { name: 'db · child operation' }));
    expect(screen.getByTestId('selected-span').textContent).toBe('child');
    fireEvent.click(screen.getByRole('button', { name: '展开或折叠 root operation 的子节点' }));
    expect(screen.queryByRole('button', { name: 'db · child operation' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '复位' }));
    expect(screen.getByRole('button', { name: 'db · child operation' })).toBeTruthy();
    expect(loadTraceWindow).toHaveBeenCalledTimes(1);
    expect(loadTraceWindow).toHaveBeenCalledWith('trace', props.from, props.to);
  });

  it('失败可重试并展示截断提示', async () => {
    vi.mocked(loadTraceWindow).mockRejectedValueOnce(new Error('trace failed'));
    render(<TraceDetailDrawer {...props} />);
    await screen.findByText('trace failed');
    vi.mocked(loadTraceWindow).mockResolvedValue({ rows: [root], truncated: true });
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    await waitFor(() => expect(screen.queryByText('trace failed')).toBeNull());
    expect(await screen.findByText(/仅展示此时间窗内的数据/)).toBeTruthy();
  });
});
