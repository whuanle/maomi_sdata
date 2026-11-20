import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { EventSearchExplorer } from '../EventSearchExplorer';
import { ChartWorkspaceContext } from '../ChartWorkspaceContext';
import { buildEventSearch, EVENT_DEFAULT_FIELDS, type EventSearchConfig } from '../eventSearchQuery';
import { chartToPanel, panelEditorDefinition } from '../chartPanel';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/search', async (original) => ({ ...await original<typeof import('@/api/search')>(), searchSql: vi.fn() }));
vi.mock('@/pages/search/SqlEditor', () => ({ SqlEditor: ({ value }: { value: string }) => <pre>{value}</pre> }));
vi.mock('@/pages/search/ColumnPicker', () => ({
  SelectColumnsPopover: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => <input aria-label="SELECT" value={value} onChange={(event) => onChange(event.target.value)} />,
  SortEditorPopover: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => <input aria-label="ORDER BY" value={value} onChange={(event) => onChange(event.target.value)} />,
}));
vi.mock('@/pages/search/WhereSuggestInput', () => ({ WhereSuggestInput: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => <input aria-label="WHERE" value={value} onChange={(event) => onChange(event.target.value)} /> }));
vi.mock('@/pages/search/ResultsTable', () => ({
  ResultsTable: (props: import('@/pages/search/ResultsTable').ResultsTableProps) => <div>
    {props.rows.map((row, index) => <button key={index} onClick={() => props.onRowOpen(row)}>{String(row.body)}</button>)}
    {props.hasMore && <button onClick={props.onLoadMore}>next page</button>}
    {props.loadMoreError && <span role="alert">{props.loadMoreError}</span>}
    <button onClick={() => props.onFilterByValue('service', "api'quoted")}>filter value</button>
    {props.complete && <span>complete</span>}
  </div>,
}));
vi.mock('@/pages/search/LogDetailDrawer', () => ({ LogDetailDrawer: ({ row }: { row: Record<string, unknown> }) => <div role="dialog">{String(row.body)}</div> }));
const response = { columns: ['ts', 'service', 'severity', 'body', 'id'], rows: [['2026-11-18T01:00:00Z', 'api', 'INFO', 'first event', '123']], elapsedMs: 3, truncated: false };

describe('EventSearchExplorer', () => {
  beforeEach(() => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    vi.mocked(searchSql).mockReset().mockResolvedValue(response);
    useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
  });

  it('invalidates a slow response on source change and restores source drafts', async () => {
    let finish!: (value: typeof response) => void;
    vi.mocked(searchSql).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    render(<EventSearchExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    await act(async () => finish(response));
    expect(screen.getByTestId('chart-event-run')).not.toBeDisabled();
    expect(screen.queryByText(/已执行：/)).toBeNull();
    expect(searchSql).toHaveBeenCalledTimes(1);
  });
  it('completes first activation under StrictMode without duplicate requests', async () => {
    render(<StrictMode><EventSearchExplorer active name="" onNameChange={vi.fn()} /></StrictMode>);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId('chart-event-run')).not.toBeDisabled());
  });
  it('shows help only for Lucene without running on toggle', async () => {
    render(<EventSearchExplorer active name="" onNameChange={() => {}} />);
    await screen.findByText('first event');
    expect(screen.getByTestId('chart-lucene-help')).toBeInTheDocument();
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: '使用说明' }));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.keyDown(screen.getByRole('combobox', { name: '查询语言' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^SQL$/ }));
    expect(screen.queryByTestId('chart-lucene-help')).toBeNull();
  });

  it.each([
    { domain: 'metrics' as const, label: '指标', where: 'name:cpu', value: 'cpu' },
    { domain: 'sessions' as const, label: '客户端会话', where: 'domain:example.com', value: 'example.com' },
  ])('runs $domain Lucene and retains the executed query for continuation', async ({ domain, label, where, value }) => {
    const sourceResponse = { columns: EVENT_DEFAULT_FIELDS[domain].split(', '), rows: [[...Array(5)].map(() => 'first')], elapsedMs: 1, truncated: true };
    render(<EventSearchExplorer active name="" onNameChange={vi.fn()} />);
    await screen.findByText('first event');
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: label }));
    expect(screen.getByRole('combobox', { name: '查询语言' })).toHaveTextContent('SQL');
    fireEvent.keyDown(screen.getByRole('combobox', { name: '查询语言' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^Lucene$/ }));
    fireEvent.change(screen.getByLabelText('WHERE'), { target: { value: where } });
    expect(screen.getByTestId('chart-lucene-help')).toBeInTheDocument();
    expect(searchSql).toHaveBeenCalledTimes(1);
    vi.mocked(searchSql).mockResolvedValueOnce(sourceResponse).mockResolvedValueOnce({ ...sourceResponse, rows: [], truncated: false });
    fireEvent.click(screen.getByTestId('chart-event-run'));
    await screen.findByRole('button', { name: '加载更多事件' });
    const executed = vi.mocked(searchSql).mock.calls[1][0];
    expect(Object.values(executed.parameters ?? {})).toContain(value);
    expect(executed.sql).not.toContain(value);
    fireEvent.change(screen.getByLabelText('WHERE'), { target: { value: 'severity:ERROR' } });
    fireEvent.click(screen.getByRole('button', { name: '加载更多事件' }));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(3));
    expect(vi.mocked(searchSql).mock.calls[2][0]).toMatchObject({
      sql: executed.sql, from: executed.from, to: executed.to, parameters: { ...executed.parameters, page_offset: 1 },
    });
    fireEvent.click(screen.getByTestId('chart-event-run'));
    expect(screen.getByRole('alert')).toHaveTextContent('未知字段');
    expect(searchSql).toHaveBeenCalledTimes(3);
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: '日志' }));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: label }));
    expect(screen.getByRole('combobox', { name: '查询语言' })).toHaveTextContent('Lucene');
    expect(screen.getByLabelText('WHERE')).toHaveValue('severity:ERROR');
  });

  it.each(['metrics', 'sessions'] as const)('restores a saved %s Lucene search and executes its bound query', async (domain) => {
    const config: EventSearchConfig = { domain, fields: EVENT_DEFAULT_FIELDS[domain], syntax: 'lucene', where: domain === 'sessions' ? 'domain:example.com' : 'name:cpu', orderBy: 'ts DESC', filters: [] };
    const built = buildEventSearch(config);
    const editor = { version: 1 as const, mode: 'builder' as const, chartType: 'search' as const, source: domain, series: [], formulas: [], groupBy: [], granularity: 'auto' as const, eventSearch: config };
    const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' };
    const panel = chartToPanel({ name: 'Saved search', chartType: 'search', source: domain, series: [], range, display: 'line', showLegend: false, autoGranularity: false, sql: built.panelSql, parameters: built.parameters, editor });
    const definition = panelEditorDefinition(JSON.parse(JSON.stringify(panel)));
    expect(definition?.eventSearch).toEqual(config);
    render(<ChartWorkspaceContext.Provider value={{ definition, range }}>
      <EventSearchExplorer active name="Saved search" onNameChange={vi.fn()} />
    </ChartWorkspaceContext.Provider>);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('combobox', { name: '查询语言' })).toHaveTextContent('Lucene');
    expect(screen.getByLabelText('WHERE')).toHaveValue(config.where);
    expect(vi.mocked(searchSql).mock.calls[0][0]).toMatchObject({ sql: built.sql, parameters: built.parameters, from: new Date(range.from).toISOString(), to: new Date(range.to).toISOString() });
    expect(panel.query?.parameters).toEqual(built.parameters);
    expect(panel.query?.sql).not.toContain('$page_');
  });

  it('runs one event request without aggregation controls or duplicate samples, and opens details', async () => {
    render(<EventSearchExplorer active name="" onNameChange={() => {}} />);
    await screen.findByText('first event');
    expect(searchSql).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('添加序列')).toBeNull();
    expect(screen.queryByText('匹配事件样本')).toBeNull();
    expect(screen.getByLabelText('SELECT')).toHaveValue('ts, service, severity, body');
    fireEvent.click(screen.getByText('first event'));
    expect(screen.getByRole('dialog')).toHaveTextContent('first event');
  });

  it('keeps edits across tab visibility without rerunning', async () => {
    const view = render(<EventSearchExplorer active name="" onNameChange={() => {}} />);
    await screen.findByText('first event');
    fireEvent.change(screen.getByLabelText('WHERE'), { target: { value: 'severity:ERROR' } });
    view.rerender(<EventSearchExplorer active={false} name="" onNameChange={() => {}} />);
    view.rerender(<EventSearchExplorer active name="" onNameChange={() => {}} />);
    expect(screen.getByLabelText('WHERE')).toHaveValue('severity:ERROR');
    expect(searchSql).toHaveBeenCalledTimes(1);
  });

  it('locks continuation to executed conditions and retries the same offset after failure', async () => {
    vi.mocked(searchSql).mockResolvedValueOnce({ ...response, truncated: true })
      .mockRejectedValueOnce(new Error('page failed'))
      .mockResolvedValueOnce({ ...response, rows: [['2026-11-18T00:59:00Z', 'api', 'INFO', 'second event', '124']] });
    render(<EventSearchExplorer active name="" onNameChange={() => {}} />);
    await screen.findByText('first event');
    fireEvent.change(screen.getByLabelText('WHERE'), { target: { value: 'severity:ERROR' } });
    fireEvent.click(screen.getByText('next page'));
    await screen.findByText('page failed');
    fireEvent.click(screen.getByText('next page'));
    await screen.findByText('second event');
    const calls = vi.mocked(searchSql).mock.calls;
    expect(calls[1][0].sql).toBe(calls[0][0].sql);
    expect(calls[1][0].from).toBe(calls[0][0].from);
    expect(calls[1][0].parameters?.page_offset).toBe(1);
    expect(calls[2][0].parameters?.page_offset).toBe(1);
    expect(screen.getByText('complete')).toBeTruthy();
  });

  it('binds field filters and discards late results from an older execution', async () => {
    let finish!: (value: typeof response) => void;
    vi.mocked(searchSql).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; })).mockResolvedValue(response);
    render(<EventSearchExplorer active name="" onNameChange={() => {}} />);
    fireEvent.click(screen.getByTestId('chart-event-run'));
    await screen.findByText('first event');
    finish({ ...response, rows: [['2026-11-18T01:00:00Z', 'api', 'INFO', 'stale event', '123']] });
    await waitFor(() => expect(screen.queryByText('stale event')).toBeNull());
    fireEvent.click(screen.getByText('filter value'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(3));
    const request = vi.mocked(searchSql).mock.calls[2][0];
    expect(request.sql).not.toContain("api'quoted");
    expect(Object.values(request.parameters ?? {})).toContain("api'quoted");
  });
});
