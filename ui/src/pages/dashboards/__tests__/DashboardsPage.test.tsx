import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { DashboardsPage } from '../DashboardsPage';
import { queryDashboards, saveDashboard } from '@/api/dashboards';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/dashboards', () => ({
  queryDashboards: vi.fn(),
  saveDashboard: vi.fn(),
  deleteDashboard: vi.fn(),
}));

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test', role: 'admin', username: 'admin' });
  vi.mocked(queryDashboards).mockResolvedValue({ page: 1, pageSize: 100, count: 1, data: [{
    id: 100, name: '验证仪表盘', description: '', kind: 'view', configJson: '{}',
    createTime: '2026-11-18T00:00:00Z', updateTime: '2026-11-18T00:00:00Z',
  }] });
  vi.mocked(saveDashboard).mockResolvedValue({ value: 101 });
});

function setup() {
  const router = createMemoryRouter([
    { path: '/dashboards', element: <DashboardsPage /> },
    { path: '/dashboards/:id', element: <div>画布详情</div> },
  ], { initialEntries: ['/dashboards'] });
  render(<RouterProvider router={router} />);
  return router;
}

describe('Dashboards 列表', () => {
  it('打开独立详情而不是弹窗', async () => {
    const router = setup();
    fireEvent.click(await screen.findByText('验证仪表盘'));
    expect(queryDashboards).toHaveBeenCalledWith({ kind: 'view', page: 1, pageSize: 100 });
    await waitFor(() => expect(router.state.location.pathname).toBe('/dashboards/100'));
  });
  it('新建空画布并跳转编辑态，不要求 JSON', async () => {
    const router = setup();
    fireEvent.click(screen.getByText('新建 Dashboard'));
    fireEvent.change(screen.getByRole('textbox', { name: '名称' }), { target: { value: '新看板' } });
    expect(screen.queryByText('Config JSON')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^新建$/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/dashboards/101'));
    expect(router.state.location.search).toBe('?edit=1');
    expect(JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!)).toMatchObject({ version: 2, panels: [] });
  });
  it('只读用户无新建和删除操作', async () => {
    useAuthStore.setState({ role: 'readonly' });
    setup();
    await screen.findByText('验证仪表盘');
    expect(screen.queryByText('新建 Dashboard')).not.toBeInTheDocument();
    expect(screen.queryByText('删除')).not.toBeInTheDocument();
  });
  it('卡片导出生成整盘 JSON 下载', async () => {
    const config = { version: 2, time: { preset: '1h' }, refreshSeconds: 0, panels: [{ id: 'p1', title: 'CPU', type: 'line', layout: { w: 6, h: 4, x: 0, y: 0 }, query: { kind: 'metric', name: 'cpu', aggregation: 'avg' } }] };
    vi.mocked(queryDashboards).mockResolvedValue({ page: 1, pageSize: 100, count: 1, data: [{
      id: 100, name: '验证仪表盘', description: '', kind: 'view', configJson: JSON.stringify(config),
      createTime: '2026-11-18T00:00:00Z', updateTime: '2026-11-18T00:00:00Z',
    }] });
    const previousCreate = URL.createObjectURL;
    const previousRevoke = URL.revokeObjectURL;
    let exportedBlob: Blob | undefined;
    const createObjectURL = vi.fn((blob: Blob) => { exportedBlob = blob; return 'blob:mock'; });
    URL.createObjectURL = createObjectURL as typeof URL.createObjectURL;
    URL.revokeObjectURL = vi.fn() as typeof URL.revokeObjectURL;
    try {
      setup();
      fireEvent.click(await screen.findByText('导出'));
      const text = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result));
        reader.onerror = () => reject(reader.error);
        reader.readAsText(exportedBlob!);
      });
      expect(text).toContain('"kind": "sdata-dashboard"');
    } finally {
      URL.createObjectURL = previousCreate;
      URL.revokeObjectURL = previousRevoke;
    }
  });
  it('导入弹窗解析预填并保存整盘', async () => {
    const router = setup();
    fireEvent.click(screen.getByText('导入'));
    const exported = JSON.stringify({ kind: 'sdata-dashboard', version: 1, exportedAt: '2026-11-20T00:00:00Z', name: '外来盘', description: '来自文件', dashboard: { version: 2, time: { preset: '1h' }, refreshSeconds: 0, panels: [{ id: 'p1', title: 'CPU', type: 'line', layout: { w: 6, h: 4, x: 0, y: 0 }, query: { kind: 'metric', name: 'cpu', aggregation: 'avg' } }] } });
    fireEvent.change(screen.getByRole('textbox', { name: 'JSON 来源' }), { target: { value: exported } });
    expect(await screen.findByText('共 1 个面板')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: '名称' })).toHaveValue('外来盘');
    const buttons = screen.getAllByRole('button', { name: '导入' });
    fireEvent.click(buttons[buttons.length - 1]);
    await waitFor(() => expect(saveDashboard).toHaveBeenCalled());
    const payload = vi.mocked(saveDashboard).mock.calls[0][0];
    expect(payload.name).toBe('外来盘');
    expect(payload.description).toBe('来自文件');
    expect(JSON.parse(payload.configJson!)).toMatchObject({ version: 2, panels: [{ id: 'p1', title: 'CPU' }] });
    await waitFor(() => expect(queryDashboards).toHaveBeenCalledTimes(2));
    expect(router.state.location.pathname).toBe('/dashboards');
  });
  it('导入非法 JSON 提示且不保存', async () => {
    setup();
    fireEvent.click(screen.getByText('导入'));
    fireEvent.change(screen.getByRole('textbox', { name: 'JSON 来源' }), { target: { value: 'not-json' } });
    expect(screen.getByText('JSON 解析失败或格式不受支持')).toBeInTheDocument();
    const buttons = screen.getAllByRole('button', { name: '导入' });
    fireEvent.click(buttons[buttons.length - 1]);
    expect(saveDashboard).not.toHaveBeenCalled();
  });
  it('只读用户可导出不可导入', async () => {
    useAuthStore.setState({ role: 'readonly' });
    setup();
    await screen.findByText('验证仪表盘');
    expect(screen.getByText('导出')).toBeInTheDocument();
    expect(screen.queryByText('导入')).not.toBeInTheDocument();
  });
});
