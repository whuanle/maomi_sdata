import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { AnalyticsPage } from '../AnalyticsPage';
import { listAnalyticsTables, listSavedQueries, saveQuery } from '@/api/analytics';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/analytics', () => ({
  listAnalyticsTables: vi.fn(), listSavedQueries: vi.fn(), saveQuery: vi.fn(), deleteSavedQuery: vi.fn(),
}));
vi.mock('@/api/search', async (original) => ({
  ...await original<typeof import('@/api/search')>(), searchSql: vi.fn(),
}));
vi.mock('../../search/SqlEditor', () => ({
  SqlEditor: ({ value, onChange }: { value: string; onChange?: (value: string) => void }) =>
    <textarea aria-label="SQL editor" value={value} readOnly={!onChange} onChange={(event) => onChange?.(event.target.value)} />,
}));

beforeEach(() => {
  HTMLElement.prototype.scrollIntoView = vi.fn();
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
  vi.mocked(listAnalyticsTables).mockResolvedValue([{
    schema: 'main', name: 'iot_20261119', domain: 'iot', day: '2026-11-19T00:00:00Z',
    columns: [{ name: 'num_value', type: 'DOUBLE', nullable: true }],
  }]);
  vi.mocked(listSavedQueries).mockResolvedValue({ page: 1, pageSize: 200, count: 0, data: [] });
  vi.mocked(searchSql).mockResolvedValue({ columns: ['signal'], rows: [['temperature']], elapsedMs: 12, truncated: false });
  vi.mocked(saveQuery).mockResolvedValue({ value: 6 });
});

describe('Analytics workbench', () => {
  it('preserves SQL and Lucene drafts across mode switches', async () => {
    render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    fireEvent.change(screen.getByLabelText('SQL editor'), { target: { value: 'SELECT 42' } });
    fireEvent.keyDown(screen.getByTestId('analytics-mode'), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: 'Lucene' }));
    fireEvent.change(screen.getByTestId('analytics-lucene'), { target: { value: 'severity:ERROR' } });
    fireEvent.keyDown(screen.getByTestId('analytics-mode'), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: 'SQL' }));
    expect(screen.getByLabelText('SQL editor')).toHaveValue('SELECT 42');
    expect(screen.getByTestId('analytics-mode')).toHaveClass('w-24', 'shrink-0');
    expect(screen.getByTestId('analytics-query-bar')).toHaveClass('flex-col');
    fireEvent.keyDown(screen.getByTestId('analytics-mode'), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: 'Lucene' }));
    expect(screen.getByTestId('analytics-lucene')).toHaveValue('severity:ERROR');
  });

  it('does not apply a pending response after unmount', async () => {
    let resolve!: (value: Awaited<ReturnType<typeof searchSql>>) => void;
    vi.mocked(searchSql).mockReturnValueOnce(new Promise((done) => { resolve = done; }));
    const { unmount } = render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    fireEvent.click(screen.getByRole('button', { name: '执行' }));
    unmount();
    await act(async () => resolve({ columns: ['value'], rows: [['late']], elapsedMs: 1, truncated: false }));
    expect(screen.queryByText('late')).not.toBeInTheDocument();
  });

  it('loads actual schema without replacing the SQL draft', async () => {
    render(<AnalyticsPage />);
    const editor = screen.getByLabelText('SQL editor') as HTMLTextAreaElement;
    const initial = editor.value;
    fireEvent.click(await screen.findByRole('button', { name: /iot_20261119/ }));
    expect(screen.getByText(/DOUBLE/)).toHaveTextContent('NULL');
    expect(editor.value).toBe(initial);
    fireEvent.change(screen.getByLabelText('搜索表名'), { target: { value: 'missing' } });
    expect(screen.queryByRole('button', { name: /iot_20261119/ })).not.toBeInTheDocument();
  });

  it('splits catalog panes and filters fields independently from tables', async () => {
    vi.mocked(listAnalyticsTables).mockResolvedValue([
      { schema: 'main', name: 'iot_20261119', domain: 'iot', day: null, columns: [
        { name: 'device_key', type: 'VARCHAR', nullable: false },
        { name: 'num_value', type: 'DOUBLE', nullable: true },
      ] },
      { schema: 'main', name: 'logs_20261119', domain: 'logs', day: null, columns: [
        { name: 'body', type: 'VARCHAR', nullable: false },
      ] },
    ]);
    render(<AnalyticsPage />);
    fireEvent.click(await screen.findByRole('button', { name: /iot_20261119/ }));
    for (const id of ['analytics-tables-pane', 'analytics-schema-pane']) {
      expect(screen.getByTestId(id)).toHaveClass('flex-1', 'basis-0', 'min-h-0', 'overflow-hidden');
    }
    fireEvent.change(screen.getByLabelText('搜索字段名'), { target: { value: 'NUM' } });
    expect(screen.getByRole('button', { name: 'num_value' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'device_key' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /logs_20261119/ })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('搜索字段名'), { target: { value: 'missing' } });
    expect(screen.queryByRole('button', { name: 'num_value' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /logs_20261119/ }));
    expect(screen.getByLabelText('搜索字段名')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'body' })).toBeInTheDocument();
  });

  it('passes row limit and bound parameters to the search executor', async () => {
    render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    fireEvent.change(screen.getByLabelText('SQL editor'), { target: { value: 'SELECT $value' } });
    fireEvent.change(screen.getByLabelText('绑定参数（JSON）'), { target: { value: '{"value":42}' } });
    fireEvent.change(screen.getByLabelText('行数上限'), { target: { value: '25' } });
    fireEvent.click(screen.getByRole('button', { name: '执行' }));
    await waitFor(() => expect(searchSql).toHaveBeenCalledWith(expect.objectContaining({
      sql: 'SELECT $value', rowLimit: 25, parameters: { value: 42 },
    })));
    expect(await screen.findByText('temperature')).toBeInTheDocument();
  });

  it('shows execution errors inline and hides stale results', async () => {
    vi.mocked(searchSql).mockRejectedValue(new Error('bad query'));
    render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    fireEvent.click(screen.getByRole('button', { name: '执行' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('bad query');
    expect(screen.queryByText('temperature')).not.toBeInTheDocument();
  });

  it('isolates saved kinds and loads legacy SQL without executing', async () => {
    vi.mocked(listSavedQueries).mockImplementation(async (query) => ({
      page: 1, pageSize: 200, count: 1, data: query?.kind === 'sql' ? [{
        id: 5, name: '旧查询', kind: 'sql', sqlText: 'SELECT 1', definition: '', description: '',
        createTime: '2026-01-01T00:00:00Z', updateTime: '2026-01-01T00:00:00Z',
      }] : [],
    }));
    render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    fireEvent.click(screen.getByRole('button', { name: '已保存查询' }));
    expect(await screen.findByText('旧查询')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '载入' }));
    expect(screen.getByLabelText('SQL editor')).toHaveValue('SELECT 1');
    expect(searchSql).not.toHaveBeenCalled();
    expect(listSavedQueries).toHaveBeenCalledWith({ kind: 'analytics', pageSize: 200 });
  });

  it('saves a versioned workbench definition', async () => {
    render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    fireEvent.click(screen.getByRole('button', { name: '保存查询' }));
    fireEvent.change(screen.getByLabelText(/名称/), { target: { value: '工作台' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(saveQuery).toHaveBeenCalledWith(expect.objectContaining({
      name: '工作台', kind: 'analytics', definition: expect.stringContaining('"version":1'),
    })));
  });

  it('readonly users can query but cannot save', async () => {
    useAuthStore.setState({ role: 'readonly' });
    render(<AnalyticsPage />);
    await screen.findByRole('button', { name: /iot_20261119/ });
    expect(screen.queryByRole('button', { name: '保存查询' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '执行' })).toBeInTheDocument();
  });

  it('reports catalog errors and supports retry', async () => {
    vi.mocked(listAnalyticsTables).mockRejectedValueOnce(new Error('catalog unavailable'));
    render(<AnalyticsPage />);
    expect(await screen.findByRole('alert')).toHaveTextContent('catalog unavailable');
    fireEvent.click(screen.getByRole('button', { name: '刷新' }));
    expect(await screen.findByRole('button', { name: /iot_20261119/ })).toBeInTheDocument();
  });
});
