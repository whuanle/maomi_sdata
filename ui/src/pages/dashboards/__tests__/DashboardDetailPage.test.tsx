import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { DashboardDetailPage } from '../DashboardDetailPage';
import { DashboardSession } from '../DashboardSession';
import { PanelRoutePage } from '../PanelRoutePage';
import { getDashboard, getDashboardData, saveDashboard, previewPanel } from '@/api/dashboards';
import { useAuthStore } from '@/store/auth';
import type { PanelConfig } from '@/api/types';

vi.mock('@/api/dashboards', async (original) => ({
  ...(await original<typeof import('@/api/dashboards')>()),
  getDashboard: vi.fn(), getDashboardData: vi.fn(), saveDashboard: vi.fn(), previewPanel: vi.fn(),
}));
vi.mock('react-grid-layout', () => ({
  GridLayout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  useContainerWidth: () => ({ width: 1200, mounted: true, containerRef: { current: null } }), noCompactor: {},
}));
vi.mock('../ChartPanelEditor', () => ({
  ChartPanelEditor: ({ initial, onApply, onClose }: { initial?: PanelConfig; onApply: (p: unknown) => void; onClose: () => void }) =>
    <><button onClick={() => { onApply({ id: 'new', title: 'CPU', type: 'stat', query: { kind: 'metric', name: 'cpu_usage' } }); onClose(); }}>确认添加</button>
      {initial && <><button onClick={() => { onApply({ ...initial, title: '新展示名称', editor: undefined }); onClose(); }}>只改展示</button>
        <button onClick={() => { onApply({ ...initial, rowLimit: 10 }); onClose(); }}>修改行上限</button></>}</>,
}));
beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test', role: 'admin', username: 'admin' });
  vi.mocked(getDashboard).mockResolvedValue({ id: 100, name: '测试画布', kind: 'view', description: '', configJson: '{"version":2,"panels":[],"refreshSeconds":0}', createTime: '', updateTime: '' });
  vi.mocked(getDashboardData).mockResolvedValue({ panels: [] });
  vi.mocked(previewPanel).mockResolvedValue({ id: 'new', series: [], columns: ['value'], rows: [[12]], elapsedMs: 1 });
  vi.mocked(saveDashboard).mockResolvedValue({ value: 100 });
});
function setup(present = false, edit = true) {
  const router = createMemoryRouter([
    { path: '/dashboards/:id', element: <DashboardSession />, children: [
      { index: true, element: <DashboardDetailPage present={present} /> },
      { path: 'panels/new', element: <PanelRoutePage /> },
      { path: 'panels/:panelId/edit', element: <PanelRoutePage /> },
    ] },
    { path: '/dashboards', element: <div>列表</div> },
  ], { initialEntries: [edit ? '/dashboards/100?edit=1' : '/dashboards/100'] });
  render(<RouterProvider router={router} />);
  return router;
}
describe('Dashboard draft', () => {
  it('uses the address as the edit-mode source for browser history', async () => {
    const router = setup(false, false);
    await screen.findByText('测试画布');
    fireEvent.click(screen.getByRole('button', { name: '编辑面板' }));
    await screen.findByRole('textbox', { name: '名称' });
    expect(router.state.location.search).toContain('edit=1');
    await act(async () => { await router.navigate(-1); });
    expect(screen.queryByRole('textbox', { name: '名称' })).not.toBeInTheDocument();
    await act(async () => { await router.navigate(1); });
    expect(screen.getByRole('textbox', { name: '名称' })).toBeInTheDocument();
  });
  it('switches grouped tabs without dirtying or querying and saves v3 member edits', async () => {
    vi.mocked(getDashboard).mockResolvedValue({ id: 100, name: '测试画布', kind: 'view', description: '', configJson: JSON.stringify({
      version: 3, refreshSeconds: 0, panels: [{ id: 'g', title: 'Grouped', type: 'group', layout: { x: 0, y: 0, w: 12, h: 4 },
        defaultPanelId: 'a', panels: [
          { id: 'a', title: 'Tab A', type: 'markdown', markdown: 'alpha' },
          { id: 'b', title: 'Tab B', type: 'markdown', markdown: 'beta' },
        ] }],
    }), createTime: '', updateTime: '' });
    setup();
    await screen.findByRole('tab', { name: 'Tab A' });
    await waitFor(() => expect(getDashboardData).toHaveBeenCalledOnce());
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Tab B' }), { button: 0, ctrlKey: false });
    expect(screen.getByText('beta')).toBeInTheDocument();
    expect(screen.queryByText('有未保存的修改')).not.toBeInTheDocument();
    expect(getDashboardData).toHaveBeenCalledOnce();
    expect(previewPanel).not.toHaveBeenCalled();
    const member = screen.getByRole('tabpanel', { name: 'Tab B' }).previousElementSibling!;
    fireEvent.click(Array.from(member.querySelectorAll('button')).find(button => button.textContent === '编辑')!);
    fireEvent.click(await screen.findByText('只改展示'));
    expect(await screen.findByRole('tab', { name: '新展示名称' })).toBeInTheDocument();
    // 独立编辑页返回会重新挂载画布并刷新基线数据，Tab 切换本身不取数.
    expect(getDashboardData).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole('button', { name: /^保存$/ }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const saved = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!);
    expect(saved.version).toBe(3);
    expect(saved.panels[0].panels[1].title).toBe('新展示名称');
    expect(saved.panels[0].defaultPanelId).toBe('a');
  });
  it('does not requery display edits but previews row-limit changes', async () => {
    vi.mocked(getDashboard).mockResolvedValue({ id: 100, name: '测试画布', kind: 'view', description: '', configJson: JSON.stringify({ version: 2, refreshSeconds: 0, panels: [{ id: 'p', title: 'Panel', type: 'table', layout: { x: 0, y: 0, w: 6, h: 4 }, rowLimit: 50, query: { kind: 'sql', sql: 'SELECT 1' } }] }), createTime: '', updateTime: '' });
    setup();
    await screen.findByText('Panel');
    await waitFor(() => expect(getDashboardData).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: /^编辑$/ }));
    fireEvent.click(await screen.findByText('只改展示'));
    await screen.findByText('新展示名称');
    expect(getDashboardData).toHaveBeenCalledTimes(2);
    expect(previewPanel).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /^编辑$/ }));
    fireEvent.click(await screen.findByText('修改行上限'));
    await waitFor(() => expect(previewPanel).toHaveBeenCalledOnce());
    expect(vi.mocked(previewPanel).mock.calls[0][0].rowLimit).toBe(10);
    expect(saveDashboard).not.toHaveBeenCalled();
  });
  it('uses a compact header and keeps Run functional', async () => {
    setup(false, false);
    const name = await screen.findByText('测试画布');
    expect(name).toHaveClass('truncate');
    expect(name.closest('.bg-bg')).toHaveClass('px-3', 'pt-2', 'gap-2');
    expect(name.closest('.bg-bg')).not.toHaveClass('overflow-auto', 'p-5');
    const run = screen.getByRole('button', { name: /^运行$/ });
    const toolbar = run.parentElement!;
    expect(toolbar).toHaveClass('flex-wrap', 'border-b', 'pb-2');
    expect(name.parentElement).toBe(toolbar);
    const present = screen.getByRole('button', { name: /^大屏展示$/ });
    const timeRange = screen.getByRole('button', { name: 'time range' });
    const refresh = screen.getByRole('combobox', { name: '刷新' });
    expect(present.parentElement!.nextElementSibling).toBe(timeRange);
    expect(timeRange.nextElementSibling).toBe(refresh);
    expect(refresh.nextElementSibling).toBe(run);
    expect(run.nextElementSibling).toBe(screen.getByRole('button', { name: '编辑面板' }));
    await waitFor(() => expect(getDashboardData).toHaveBeenCalledOnce());
    await waitFor(() => expect(run).not.toBeDisabled());
    fireEvent.click(run);
    await waitFor(() => expect(getDashboardData).toHaveBeenCalledTimes(2));
    expect(screen.getByText('编辑面板')).toBeInTheDocument();
  });
  it('keeps editing actions immediately after the name without a description field', async () => {
    setup();
    const name = await screen.findByRole('textbox', { name: '名称' });
    const cancel = screen.getByRole('button', { name: /^取消$/ });
    const actions = cancel.parentElement!;
    expect(name).toHaveClass('w-80', 'max-w-full');
    expect(name.parentElement).not.toHaveClass('flex-1');
    expect(name.nextElementSibling).toBe(actions);
    expect(actions).not.toHaveClass('ml-auto');
    expect(Array.from(actions.querySelectorAll('button')).map((button) => button.textContent)).toEqual(['取消', '保存', '大屏展示', '导出']);
    expect(screen.getByRole('button', { name: '添加分组' }).nextElementSibling).toBe(screen.getByRole('button', { name: '导入面板' }));
    expect(screen.getByRole('button', { name: '导入面板' }).nextElementSibling).toBe(screen.getByRole('button', { name: '自定义组件' }));
    expect(screen.getByRole('button', { name: '自定义组件' }).nextElementSibling).toBe(screen.getByTestId('add-component'));
    expect(screen.queryByRole('textbox', { name: '描述' })).not.toBeInTheDocument();
    fireEvent.change(name, { target: { value: '更新名称' } });
    expect(name.nextElementSibling).toBe(actions);
    fireEvent.click(screen.getByRole('button', { name: /^保存$/ }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    expect(vi.mocked(saveDashboard).mock.calls[0][0].name).toBe('更新名称');
    expect(vi.mocked(saveDashboard).mock.calls[0][0].description).toBe('');
  });
  it('retains long names without expanding the header', async () => {
    const name = '很长的仪表盘名称'.repeat(8);
    vi.mocked(getDashboard).mockResolvedValue({ id: 100, name, kind: 'view', description: '', configJson: '{"version":2,"panels":[]}', createTime: '', updateTime: '' });
    setup(false, false);
    expect(await screen.findByTitle(name)).toHaveClass('truncate');
  });
  it('admin presentation stays read-only and has independent compact spacing', async () => {
    setup(true);
    const name = await screen.findByText('测试画布');
    expect(name.closest('.bg-bg')).toHaveClass('min-h-screen', 'pt-2');
    expect(screen.queryByRole('textbox', { name: '名称' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^保存$/ })).not.toBeInTheDocument();
    expect(screen.queryByTestId('add-component')).not.toBeInTheDocument();
  });
  it('adds locally and persists only on Save', async () => {
    setup();
    await screen.findByDisplayValue('测试画布');
    fireEvent.click(screen.getAllByText('添加组件')[0]);
    fireEvent.click(await screen.findByText('确认添加'));
    await screen.findByText('CPU');
    expect(saveDashboard).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /^保存$/ }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const config = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!);
    expect(config.panels[0].layout).toEqual({ x: 0, y: 0, w: 6, h: 4 });
    expect(config.version).toBe(2);
  });
  it('failed saves retain the draft', async () => {
    vi.mocked(saveDashboard).mockRejectedValue(new Error('save failed'));
    setup();
    await screen.findByDisplayValue('测试画布');
    fireEvent.change(screen.getByRole('textbox', { name: '名称' }), { target: { value: '未保存名称' } });
    fireEvent.click(screen.getByRole('button', { name: /^保存$/ }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    expect(screen.getByDisplayValue('未保存名称')).toBeInTheDocument();
    expect(screen.getByText('有未保存的修改')).toBeInTheDocument();
  });
  it('readonly ignores edit query and presentation has no editing controls', async () => {
    useAuthStore.setState({ role: 'readonly' });
    setup(true);
    await screen.findByText('测试画布');
    expect(screen.queryByText('添加组件')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^保存$/ })).not.toBeInTheDocument();
  });
});
