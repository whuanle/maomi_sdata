import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { PatternsExplorer } from '../PatternsExplorer';
import { searchSql } from '@/api/search';

vi.mock('@/api/search', async (original) => ({ ...await original<typeof import('@/api/search')>(), searchSql: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="pattern-trend" /> }));
vi.mock('@/pages/search/SqlEditor', () => ({ SqlEditor: ({ value }: { value: string }) => <pre>{value}</pre> }));
vi.mock('@/pages/search/LogDetailDrawer', () => ({ LogDetailDrawer: ({ row, onFilterByValue }: { row: Record<string, unknown>; onFilterByValue: (field: string, value: unknown) => void }) => <div role="dialog">{String(row.body)}<button onClick={() => onFilterByValue('service', 'api')}>filter sample</button></div> }));
const response = { columns: ['pattern', 'level', 'count', 'sample', 'trend', 'total_patterns'],
  rows: [['订单 <*>', 'info', 2, '{"body":"订单 12"}', '[{"bucket":1,"count":2}]', 1]], elapsedMs: 2, truncated: false };
function mount(active = true) { return render(<PatternsExplorer active={active} name="" onNameChange={() => {}} />); }

describe('PatternsExplorer', () => {
  beforeEach(() => { HTMLElement.prototype.scrollIntoView = vi.fn(); vi.mocked(searchSql).mockReset().mockResolvedValue(response); });
  it('invalidates a slow response on source change and restores source drafts', async () => {
    let finish!: (value: typeof response) => void;
    vi.mocked(searchSql).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    render(<PatternsExplorer active name="" onNameChange={vi.fn()} />);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^指标$/ }));
    await act(async () => finish(response));
    expect(screen.getByTestId('patterns-run')).not.toBeDisabled();
    expect(screen.queryByText(/已执行：/)).toBeNull();
    expect(searchSql).toHaveBeenCalledTimes(1);
  });
  it('completes first activation under StrictMode without duplicate requests', async () => {
    render(<StrictMode><PatternsExplorer active name="" onNameChange={vi.fn()} /></StrictMode>);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId('patterns-run')).not.toBeDisabled());
  });
  it('keeps drilldown filters and the executed window on the next run', async () => {
    mount(); await screen.findByText('订单');
    fireEvent.click(screen.getByTitle('查看代表事件'));
    fireEvent.click(screen.getByText('filter sample'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByTestId('patterns-run')).not.toBeDisabled());
    const filtered = vi.mocked(searchSql).mock.calls[1][0];
    expect(Object.values(filtered.parameters ?? {})).toContain('api');
    fireEvent.click(screen.getByTestId('patterns-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(3));
    const repeated = vi.mocked(searchSql).mock.calls[2][0];
    expect(repeated.from).toBe(filtered.from);
    expect(repeated.to).toBe(filtered.to);
    expect(Object.values(repeated.parameters ?? {})).toContain('api');
    expect(repeated.sql).not.toContain("'api'");
    await waitFor(() => expect(screen.getByTestId('patterns-run')).not.toBeDisabled());
    for (let index = 0; index < 8; index++) {
      fireEvent.click(screen.getByTitle('查看代表事件'));
      fireEvent.click(screen.getByText('filter sample'));
      await waitFor(() => expect(screen.getByTestId('patterns-run')).not.toBeDisabled());
    }
    const calls = vi.mocked(searchSql).mock.calls;
    expect(Object.keys(calls[calls.length - 1][0].parameters ?? {}).length).toBeLessThan(32);
  });
  it('shows help for Lucene WHERE and hides it for SQL without running', async () => {
    mount(); await screen.findByText('订单');
    expect(screen.getByTestId('chart-lucene-help')).toBeInTheDocument();
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: '使用说明' }));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.keyDown(screen.getByRole('combobox', { name: '查询语言' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^SQL$/ }));
    expect(screen.queryByTestId('chart-lucene-help')).toBeNull();
  });

  it('loads only on first activation and renders dedicated controls and details', async () => {
    const view = mount(false);
    expect(searchSql).not.toHaveBeenCalled();
    view.rerender(<PatternsExplorer active name="" onNameChange={() => {}} />);
    expect(await screen.findByText('订单')).toBeInTheDocument();
    expect(screen.getByText('模式表达式')).toBeInTheDocument();
    expect(screen.queryByText('添加序列')).not.toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('查看代表事件'));
    expect(await screen.findByRole('dialog')).toHaveTextContent('订单 12');
    expect(searchSql).toHaveBeenCalledTimes(1);
  });
  it('binds Lucene values and rejects invalid pattern expressions without requests', async () => {
    mount(); await screen.findByText('订单');
    fireEvent.change(screen.getByLabelText('条件'), { target: { value: 'service:api' } });
    fireEvent.click(screen.getByTestId('patterns-run'));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    expect(vi.mocked(searchSql).mock.calls[1][0].parameters).toMatchObject({ p0: 'api' });
    await waitFor(() => expect(screen.getByTestId('patterns-run')).not.toBeDisabled());
    fireEvent.change(screen.getByLabelText('模式表达式'), { target: { value: 'read_text(body)' } });
    fireEvent.click(screen.getByTestId('patterns-run'));
    expect(await screen.findByRole('alert')).toHaveTextContent('模式表达式仅支持');
    expect(searchSql).toHaveBeenCalledTimes(2);
  });
  it('toggles line wrapping, previews generated SQL and copies a template', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    mount(); await screen.findByText('订单');
    fireEvent.click(screen.getByRole('button', { name: '禁用换行' }));
    expect(screen.getByRole('button', { name: '启用换行' })).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(screen.getByRole('button', { name: '复制模板' }));
    expect(writeText).toHaveBeenCalledWith('订单 <*>');
    fireEvent.click(screen.getAllByRole('button', { name: '预览 SQL' })[0]);
    expect(await screen.findByRole('dialog')).toHaveTextContent('WITH source_events');
  });
  it('displays errors and lets a later run recover', async () => {
    vi.mocked(searchSql).mockRejectedValueOnce(new Error('query failed'));
    mount();
    expect(await screen.findByRole('alert')).toHaveTextContent('query failed');
    fireEvent.click(screen.getByTestId('patterns-run'));
    expect(await screen.findByText('订单')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
