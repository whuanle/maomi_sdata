import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import '@/i18n';
import { DashboardSession } from '../DashboardSession';
import { DashboardDetailPage } from '../DashboardDetailPage';
import { PanelDesignerPage } from '../PanelDesignerPage';
import { getDashboard, getDashboardData, previewPanel, saveDashboard } from '@/api/dashboards';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/dashboards', async original => ({
  ...(await original<typeof import('@/api/dashboards')>()),
  getDashboard: vi.fn(), getDashboardData: vi.fn(), previewPanel: vi.fn(), saveDashboard: vi.fn(),
}));
vi.mock('@/pages/search/SqlEditor', () => ({
  SqlEditor: ({ value, onChange }: { value: string; onChange: (s: string) => void }) => <textarea aria-label="SQL" value={value} onChange={e => onChange(e.target.value)} />,
}));
vi.mock('react-grid-layout', () => ({
  GridLayout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  useContainerWidth: () => ({ width: 1200, mounted: true, containerRef: { current: null } }), noCompactor: {},
}));
beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test', role: 'admin', username: 'admin' });
  vi.mocked(getDashboard).mockResolvedValue({ id: 100, name: '原名称', kind: 'view', description: '', configJson: '{"version":2,"panels":[],"refreshSeconds":0}', createTime: '', updateTime: '' });
  vi.mocked(getDashboardData).mockResolvedValue({ panels: [] });
  vi.mocked(previewPanel).mockResolvedValue({ id: 'p', series: [], columns: ['value'], rows: [[12]], elapsedMs: 1 });
  vi.mocked(saveDashboard).mockResolvedValue({ value: 100 });
});
function setup(path = '/dashboards/100?edit=1') {
  const router = createMemoryRouter([{ path: '/dashboards/:id', element: <DashboardSession />, children: [
    { index: true, element: <DashboardDetailPage /> },
    { path: 'panels/new', element: <PanelDesignerPage /> },
  ] }], { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}
it('restores display settings from the URL without persisting query contents', async () => {
  const router = setup('/dashboards/100/panels/new?settings=display&node=A');
  expect(await screen.findByRole('dialog')).toBeInTheDocument();
  expect(router.state.location.search).toContain('settings=display');
  expect(router.state.location.search).not.toContain('SELECT');
  expect(saveDashboard).not.toHaveBeenCalled();
});
it('preserves dashboard draft across designer and applies only successfully executed queries', async () => {
  setup();
  fireEvent.change(await screen.findByRole('textbox', { name: '名称' }), { target: { value: '未保存名称' } });
  fireEvent.click(screen.getByRole('button', { name: '自定义组件' }));
  const title = await screen.findByRole('textbox', { name: '组件名称' });
  fireEvent.change(title, { target: { value: '自定义比率' } });
  const apply = screen.getByRole('button', { name: '应用到仪表盘草稿' });
  expect(apply).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: '运行' }));
  await waitFor(() => expect(apply).not.toBeDisabled());
  fireEvent.change(screen.getByRole('combobox', { name: 'Lucene 筛选 A' }), { target: { value: 'service:api' } });
  expect(apply).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: '运行' }));
  await waitFor(() => expect(apply).not.toBeDisabled());
  fireEvent.click(apply);
  expect(await screen.findByDisplayValue('未保存名称')).toBeInTheDocument();
  expect(await screen.findByText('自定义比率')).toBeInTheDocument();
  expect(getDashboard).toHaveBeenCalledOnce();
  expect(saveDashboard).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: '保存' }));
  await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
  const saved = vi.mocked(saveDashboard).mock.calls[0][0];
  expect(saved.name).toBe('未保存名称');
  const node = JSON.parse(saved.configJson!).panels[0].query.custom.nodes[0];
  expect(node.editor.where).toBe('service:api');
  expect(Object.values(node.parameters)).toContain('api');
  expect(node.sql).not.toContain("'api'");
});
it('cancels an unmodified designer without discarding the dashboard draft', async () => {
  setup();
  fireEvent.change(await screen.findByRole('textbox', { name: '名称' }), { target: { value: '保留草稿' } });
  fireEvent.click(screen.getByRole('button', { name: '自定义组件' }));
  await screen.findByRole('textbox', { name: '组件名称' });
  fireEvent.click(screen.getByRole('button', { name: '取消' }));
  expect(await screen.findByDisplayValue('保留草稿')).toBeInTheDocument();
  expect(getDashboard).toHaveBeenCalledOnce();
  expect(saveDashboard).not.toHaveBeenCalled();
});
it('ignores responses for a query changed during execution', async () => {
  let resolve!: (value: Awaited<ReturnType<typeof previewPanel>>) => void;
  vi.mocked(previewPanel).mockReturnValueOnce(new Promise(done => { resolve = done; }));
  setup('/dashboards/100/panels/new');
  fireEvent.change(await screen.findByRole('textbox', { name: '组件名称' }), { target: { value: '迟到查询' } });
  fireEvent.click(screen.getByRole('button', { name: '运行' }));
  await waitFor(() => expect(previewPanel).toHaveBeenCalledOnce());
  fireEvent.change(screen.getByRole('combobox', { name: 'Lucene 筛选 A' }), { target: { value: 'service:db' } });
  resolve({ id: 'old', series: [], columns: ['value'], rows: [[12345]], elapsedMs: 1 });
  await waitFor(() => expect(screen.getByRole('button', { name: '运行' })).not.toBeDisabled());
  expect(screen.getByRole('button', { name: '应用到仪表盘草稿' })).toBeDisabled();
  expect(screen.queryByText('12345')).not.toBeInTheDocument();
});
it('combines visual inputs with a guided calculation and output selection', async () => {
  setup('/dashboards/100/panels/new');
  fireEvent.change(await screen.findByRole('textbox', { name: '组件名称' }), { target: { value: '组合数据' } });
  fireEvent.click(screen.getByRole('button', { name: '添加数据查询' }));
  fireEvent.click(screen.getByRole('button', { name: '添加表达式' }));
  expect(screen.getByRole('combobox', { name: '计算方式 C' })).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '显示全部数据' }));
  fireEvent.click(screen.getByRole('button', { name: '运行' }));
  await waitFor(() => expect(previewPanel).toHaveBeenCalled());
  const custom = vi.mocked(previewPanel).mock.calls[0][0].query!.custom!;
  expect(custom.nodes[2].expression).toBe('A / B');
  expect(custom.outputs).toEqual(['A', 'B', 'C']);
  expect(custom.nodes[0].editor!.mode).toBe('builder');
});
it('opens directly and blocks invalid parameters', async () => {
  setup('/dashboards/100/panels/new');
  fireEvent.change(await screen.findByRole('textbox', { name: '组件名称' }), { target: { value: '测试' } });
  fireEvent.click(screen.getByRole('button', { name: '高级编辑' }));
  fireEvent.change(screen.getByRole('textbox', { name: '绑定参数（JSON） A' }), { target: { value: '{broken' } });
  fireEvent.click(screen.getByRole('button', { name: '运行' }));
  expect(previewPanel).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: '应用到仪表盘草稿' })).toBeDisabled();
});
