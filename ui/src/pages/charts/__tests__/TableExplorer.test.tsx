import { StrictMode } from 'react';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { TableExplorer } from '../TableExplorer';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/search', async (original) => ({ ...(await original<typeof import('@/api/search')>()), searchSql: vi.fn() }));
vi.mock('@/pages/search/SqlEditor', () => ({ SqlEditor: ({ value, onChange, readOnly }: { value: string; onChange?: (value: string) => void; readOnly?: boolean }) => <textarea aria-label={readOnly ? "generated-sql" : "sql-editor"} value={value} onChange={(event) => onChange?.(event.target.value)} /> }));
vi.mock('../SaveChartModal', () => ({ SaveChartModal: (props: Record<string, unknown>) => <pre data-testid="saved-table">{JSON.stringify(props)}</pre> }));
const response = { columns: ['service', 'count'], rows: [['api', 3], ['db', 2]], elapsedMs: 1, truncated: false };
const mount = (active = true) => render(<TableExplorer active={active} name="Errors" onNameChange={vi.fn()} />);
const ready = () => waitFor(() => expect(screen.getByTestId('table-run')).not.toBeDisabled());

describe('TableExplorer', () => {
  it('auto-runs a grouped 24-hour default under StrictMode', async () => {
    render(<StrictMode><TableExplorer active name="Default" onNameChange={vi.fn()} /></StrictMode>);
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1)); await ready();
    const request = vi.mocked(searchSql).mock.lastCall![0];
    expect(request.sql).toContain('GROUP BY "service"');
    expect(new Date(request.to).getTime() - new Date(request.from).getTime()).toBe(86400000);
    expect(screen.getByText('api')).toBeInTheDocument();
  });
  it('switches source, clears old results immediately and ignores a late old query', async () => {
    let finish!: (value: typeof response) => void;
    vi.mocked(searchSql).mockReturnValueOnce(new Promise((resolve) => { finish = resolve; }));
    mount(); await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(1));
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(screen.getByRole('option', { name: '指标' }));
    await ready();
    const request = vi.mocked(searchSql).mock.lastCall![0];
    expect(request.sql).toContain('FROM {{metrics}}');
    expect(request.sql).toContain('GROUP BY "name"');
    finish({ ...response, rows: [['old-source', 99]] });
    await waitFor(() => expect(screen.queryByText('old-source')).not.toBeInTheDocument());
    expect(screen.getByRole('combobox', { name: '聚合字段 1' })).toHaveTextContent('value');
  });
  it('runs error preset with bound values and preserves them on save and drill-down', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: '错误服务排行' })); await ready();
    const request = vi.mocked(searchSql).mock.lastCall![0];
    expect(request.sql).not.toContain("'ERROR'");
    expect(Object.values(request.parameters ?? {})).toContain('ERROR');
    fireEvent.click(screen.getByText('api'));
    await waitFor(() => expect(vi.mocked(searchSql).mock.lastCall![0].sql).toContain('ORDER BY ts DESC'));
    expect(Object.values(vi.mocked(searchSql).mock.lastCall![0].parameters ?? {})).toEqual(expect.arrayContaining(['ERROR', 'api']));
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    expect(Object.values(JSON.parse(screen.getByTestId('saved-table').textContent!).parameters)).toContain('ERROR');
  });
  it('isolates SQL drafts by source', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'sql-editor' }), { target: { value: 'SELECT 42 FROM {{logs}}' } });
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(screen.getByRole('option', { name: '指标' })); await ready();
    expect((screen.getByRole('textbox', { name: 'sql-editor' }) as HTMLTextAreaElement).value).toContain('{{metrics}}');
    fireEvent.keyDown(screen.getByRole('combobox', { name: '数据源' }), { key: 'ArrowDown' });
    fireEvent.click(screen.getByRole('option', { name: '日志' })); await ready();
    expect(screen.getByRole('textbox', { name: 'sql-editor' })).toHaveValue('SELECT 42 FROM {{logs}}');
  });

  beforeEach(() => {
    vi.mocked(searchSql).mockReset().mockResolvedValue(response);
    useAuthStore.setState({ token: 'test', role: 'admin' });
  });
  it('runs a table without time buckets, has independent grouping and configurable Top N', async () => {
    mount(); await ready();
    fireEvent.change(screen.getByLabelText('Top N'), { target: { value: '1' } });
    fireEvent.click(screen.getByTestId('table-run')); await ready();
    const request = vi.mocked(searchSql).mock.lastCall![0];
    expect(request.sql).not.toContain('time_bucket');
    expect(request.sql).toContain('GROUP BY "service"');
    expect(Object.values(request.parameters ?? {})).toContain(2);
    expect(request.rowLimit).toBe(2);
    expect(screen.getByText('api')).toBeInTheDocument();
    expect(screen.queryByText('db')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('前 1 个分组');
  });
  it('re-queries header sort from the executed configuration, not an unrun draft', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByTestId('table-run')); await ready();
    const executed = vi.mocked(searchSql).mock.lastCall![0];
    fireEvent.keyDown(screen.getByRole('combobox', { name: '分组' }), { key: 'ArrowDown' });
    fireEvent.click(screen.getByRole('option', { name: /severity/ }));
    fireEvent.click(screen.getByRole('button', { name: /count/ })); await ready();
    const request = vi.mocked(searchSql).mock.lastCall![0];
    expect(request.sql).toContain('GROUP BY "service"');
    expect(request.sql).not.toContain('GROUP BY "severity"');
    expect(request.from).toBe(executed.from);
    expect(request.to).toBe(executed.to);
  });
  it('saves the executed Top N and stable columns, not subsequent drafts', async () => {
    mount(); await ready();
    fireEvent.change(screen.getByLabelText('Top N'), { target: { value: '2' } });
    fireEvent.click(screen.getByTestId('table-run')); await ready();
    fireEvent.change(screen.getByLabelText('Top N'), { target: { value: '50' } });
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    const saved = JSON.parse(screen.getByTestId('saved-table').textContent!);
    expect(saved.tableQuery.topN).toBe(2);
    expect(saved.tableColumns[1]).toMatchObject({ kind: 'metric', name: 'count' });
    expect(Object.values(saved.parameters)).toContain(2);
    expect(Object.values(saved.parameters)).not.toContain(3);
  });
  it('does not query inactive Table and retains draft on tab return', async () => {
    const view = mount(false);
    expect(searchSql).not.toHaveBeenCalled();
    view.rerender(<TableExplorer active name="Errors" onNameChange={vi.fn()} />); await ready();
    fireEvent.change(screen.getByLabelText('Top N'), { target: { value: '25' } });
    view.rerender(<TableExplorer active={false} name="Errors" onNameChange={vi.fn()} />);
    view.rerender(<TableExplorer active name="Errors" onNameChange={vi.fn()} />);
    expect(screen.getByLabelText('Top N')).toHaveValue(25);
    expect(searchSql).toHaveBeenCalledTimes(1);
  });
  it('binds group values for drill-down and does not replace aggregate results', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByTestId('table-run')); await ready();
    vi.mocked(searchSql).mockResolvedValueOnce({ ...response, columns: ['ts', 'body'], rows: [] });
    fireEvent.click(screen.getByText('api'));
    await waitFor(() => expect(vi.mocked(searchSql).mock.lastCall![0].sql).toContain('ORDER BY ts DESC'));
    const request = vi.mocked(searchSql).mock.lastCall![0];
    expect(request.sql).not.toContain("'api'");
    expect(Object.values(request.parameters ?? {})).toContain('api');
    expect(screen.getByText('api')).toBeInTheDocument();
  });
  it('applies stripe and header settings and hides write actions for readonly roles', async () => {
    useAuthStore.setState({ role: 'readonly' });
    mount(); await ready();
    expect(screen.queryByRole('button', { name: '保存到仪表盘' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '显示设置' }));
    const drawer = screen.getByRole('dialog');
    fireEvent.click(within(drawer).getByRole('switch', { name: '斑马纹' }));
    expect(screen.getAllByRole('row', { hidden: true })[2]).toHaveClass('bg-surface-2/50');
    fireEvent.click(within(drawer).getByRole('switch', { name: '滚动时表头吸顶' }));
    expect(screen.getAllByRole('columnheader', { hidden: true })[0]).not.toHaveClass('sticky');
  });
});
