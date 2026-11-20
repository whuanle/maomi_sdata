import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { RumPage, decodeChunks } from '../RumPage';
import { useAuthStore } from '@/store/auth';

// 断言图表配置所需的最小结构（如热力图坐标轴类型）
type CapturedChartOption = { xAxis?: { type?: string }; yAxis?: { type?: string }; series?: Array<{ type?: string; data?: unknown[] }> };
const mocks = vi.hoisted(() => ({ search: vi.fn(), projects: vi.fn(), replay: vi.fn(), events: vi.fn(), save: vi.fn(), chartOptions: [] as Array<{ xAxis?: { type?: string }; yAxis?: { type?: string }; series?: Array<{ type?: string; data?: unknown[] }> }> }));
vi.mock('@/api/rum', () => ({
  fetchRumProjects: mocks.projects,
  saveRumProject: mocks.save,
  searchRumSessions: mocks.search,
  fetchRumReplay: mocks.replay,
  fetchSessionEvents: mocks.events,
  fetchRumPerformance: vi.fn().mockResolvedValue({ pageViews: 12, uniqueVisitors: 3, vitalP75: { LCP: 1800 } }),
  fetchRumErrors: vi.fn().mockResolvedValue({ items: [] }),
  fetchRumHeatmap: vi.fn().mockResolvedValue({ cells: [], totalClicks: 0, topTargets: [] }),
  fetchRumSitemap: vi.fn().mockResolvedValue({ items: [] }),
}));
vi.mock('rrweb-player', () => ({ default: class {
  $destroy() {}
  pause() {}
  $set() {}
} }));

function renderPage(query = '') {
  return render(<MemoryRouter initialEntries={['/sessions' + query]}><RumPage /></MemoryRouter>);
}

// Chart 以桩替身渲染并捕获 option，供断言图表配置（如热力图坐标轴类型）
vi.mock('@/design-system/Chart', () => ({ Chart: ({ option }: { option: CapturedChartOption }) => {
  mocks.chartOptions.push(option);
  return <div data-testid="mock-chart" />;
} }));

describe('Client Session', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ token: 'test', username: 'admin', role: 'admin' });
    mocks.projects.mockResolvedValue({ data: [{ id: 123, projectKey: 'rum_x', name: '演示站', domainsJson: '["https://app.example.com"]', perfSampleRate: 0.5, replaySampleRate: 0.2 }] });
    mocks.search.mockResolvedValue({ count: 1, items: [{ sessionId: 'session-a', firstSeen: '2026-11-18T12:00:00Z', lastSeen: '2026-11-18T12:01:00Z', domain: 'demo.local', pageViews: 1, errorCount: 0, hasReplay: true }] });
    mocks.events.mockResolvedValue([]);
    mocks.replay.mockResolvedValue({ chunks: [] });
  });

  it('默认展示会话并将执行搜索传给服务端', async () => {
    renderPage();
    await screen.findByRole('button', { name: 'session-a' });
    expect(screen.getByRole('tab', { name: /会话|Sessions/ })).toHaveAttribute('data-state', 'active');
    const input = screen.getByRole('textbox', { name: /搜索会话|Search session/ });
    fireEvent.change(input, { target: { value: 'demo.local' } });
    fireEvent.click(screen.getByRole('button', { name: /执行|Run/ }));
    await waitFor(() => expect(mocks.search).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'demo.local', page: 1, pageSize: 20 })));
  });

  it('详情回放携带项目和固定时间，返回保留查询', async () => {
    renderPage('?project=rum_x&search=demo&from=2026-11-18T12:00:00Z&to=2026-11-18T13:00:00Z');
    fireEvent.click(await screen.findByRole('button', { name: 'session-a' }));
    await waitFor(() => expect(mocks.replay).toHaveBeenCalledWith('session-a', { projectKey: 'rum_x', from: '2026-11-18T12:00:00Z', to: '2026-11-18T13:00:00Z' }));
    fireEvent.click(screen.getByRole('button', { name: /返回会话|Back to sessions/ }));
    expect(screen.getByRole('textbox', { name: /搜索会话|Search session/ })).toHaveValue('demo');
  });

  it('支持会话深链和查询失败重试', async () => {
    mocks.events.mockRejectedValueOnce(new Error('network'));
    renderPage('?project=rum_x&session=deep-link');
    await screen.findByRole('alert');
    expect(mocks.events).toHaveBeenCalledWith(expect.objectContaining({ sessionId: 'deep-link', projectKey: 'rum_x' }));
  });

  it('查询失败不伪装成没有数据', async () => {
    mocks.search.mockRejectedValueOnce(new Error('network'));
    renderPage();
    await screen.findByRole('alert');
    mocks.search.mockResolvedValue({ count: 0, items: [] });
    fireEvent.click(screen.getByRole('alert').querySelector('button')!);
    await screen.findByText(/当前条件下没有会话|No sessions match/);
  });

  it('没有项目展示接入引导', async () => {
    mocks.projects.mockResolvedValue({ data: [] });
    renderPage();
    await screen.findByText(/配置会话回放|Set up session replays/);
    expect(mocks.search).not.toHaveBeenCalled();
  });

  it('没有项目时从工具栏直接创建并选中新项目', async () => {
    mocks.projects.mockResolvedValue({ data: [] });
    mocks.save.mockResolvedValue({});
    renderPage();
    await screen.findByText(/配置会话回放|Set up session replays/);
    fireEvent.click(screen.getByRole('button', { name: /创建项目|Create project/ }));
    const dialog = screen.getByRole('dialog', { name: /创建项目|Create project/ });
    expect(screen.queryByRole('dialog', { name: /接入配置|Integration/ })).not.toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/项目标识|Project key/), { target: { value: 'rum_new' } });
    fireEvent.change(within(dialog).getByLabelText(/项目名称|Project name/), { target: { value: '新项目' } });
    mocks.projects.mockResolvedValue({ data: [{ projectKey: 'rum_new', name: '新项目' }] });
    fireEvent.click(within(dialog).getByRole('button', { name: /创建项目|Create project/ }));
    await waitFor(() => expect(mocks.save).toHaveBeenCalledWith({
      projectKey: 'rum_new', name: '新项目', perfSampleRate: 1, replaySampleRate: 1, domainsJson: '[]',
    }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(mocks.search).toHaveBeenLastCalledWith(expect.objectContaining({ projectKey: 'rum_new' })));
    expect(mocks.projects).toHaveBeenCalledTimes(2);
  });

  it('已有项目时仍可直接创建且接入配置只展示接入帮助', async () => {
    renderPage();
    await screen.findByRole('button', { name: 'session-a' });
    expect(screen.getByRole('button', { name: /创建项目|Create project/ })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: /接入配置|Integration/ }));
    const dialog = screen.getByRole('dialog', { name: /接入配置|Integration/ });
    expect(within(dialog).getByText(/rum_x/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: /创建项目|Create project/ })).not.toBeInTheDocument();
  });

  it.each(['write', 'readonly'] as const)('%s 用户不显示创建入口但可以查看接入代码', async (role) => {
    useAuthStore.setState({ role });
    renderPage();
    await screen.findByRole('button', { name: 'session-a' });
    expect(screen.queryByRole('button', { name: /创建项目|Create project/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /接入配置|Integration/ }));
    expect(screen.getByRole('dialog', { name: /接入配置|Integration/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /创建项目|Create project/ })).not.toBeInTheDocument();
  });

  it('管理员保存允许站点且保留项目名称和采样率', async () => {
    mocks.save.mockResolvedValue({});
    renderPage();
    await screen.findByRole('button', { name: 'session-a' });
    fireEvent.click(screen.getByRole('button', { name: /接入配置|Integration/ }));
    const dialog = screen.getByRole('dialog');
    const input = within(dialog).getByLabelText(/允许站点|Allowed sites/);
    expect(input).toHaveValue('https://app.example.com');
    fireEvent.change(input, { target: { value: 'https://NEW.example.com:443\nhttp://localhost:3000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /保存允许站点|Save allowed sites/ }));
    await waitFor(() => expect(mocks.save).toHaveBeenCalledWith({
      id: 123, projectKey: 'rum_x', name: '演示站', perfSampleRate: 0.5, replaySampleRate: 0.2,
      domainsJson: '["https://new.example.com","http://localhost:3000"]',
    }));
  });

  it('非法允许站点阻止保存', async () => {
    renderPage();
    await screen.findByRole('button', { name: 'session-a' });
    fireEvent.click(screen.getByRole('button', { name: /接入配置|Integration/ }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/允许站点|Allowed sites/), { target: { value: 'https://*.example.com/path' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /保存允许站点|Save allowed sites/ }));
    await screen.findByText(/请填写不含路径|Enter full HTTP/);
    expect(mocks.save).not.toHaveBeenCalled();
  });

  it('性能视图复用已执行的绝对范围', async () => {
    renderPage();
    await screen.findByRole('button', { name: 'session-a' });
    fireEvent.mouseDown(screen.getByRole('tab', { name: /性能|Performance/ }));
    await screen.findByText('LCP: 1800');
  });

  it('跨页面分片序号重置时按事件时间恢复播放顺序', async () => {
    const decoded = await decodeChunks([
      { seq: 1, timestamp: '2026-11-18T12:01:00Z', blobBase64: btoa('[{"type":3,"timestamp":2000}]') },
      { seq: 2, timestamp: '2026-11-18T12:00:00Z', blobBase64: btoa('[{"type":2,"timestamp":1000}]') },
    ]);
    expect(decoded.events.map((event) => event.timestamp)).toEqual([1000, 2000]);
  });

  it('回放分段排序，损坏分段显式计数', async () => {
    const decoded = await decodeChunks([
      { seq: 2, timestamp: '', blobBase64: btoa('[{"type":3}]') },
      { seq: 1, timestamp: '', blobBase64: btoa('[{"type":2}]') },
      { seq: 3, timestamp: '', blobBase64: 'bad!' },
    ]);
    expect(decoded.events.map((event) => event.type)).toEqual([2, 3]);
    expect(decoded.damaged).toBe(1);
  });

  it('点击热力图坐标轴必须是类目轴（ECharts 笛卡尔 heatmap 硬性要求）', async () => {
    const { fetchRumHeatmap } = await import('@/api/rum');
    (fetchRumHeatmap as ReturnType<typeof vi.fn>).mockResolvedValue({
      cells: [{ x: 3, y: 5, count: 2 }, { x: 12, y: 30, count: 7 }, { x: 20, y: 1, count: 0 }],
      totalClicks: 9,
      topTargets: [],
    });
    renderPage('?project=rum_x&tab=heatmap');
    await screen.findByTestId('mock-chart');
    const option = mocks.chartOptions[mocks.chartOptions.length - 1]!;
    expect(option.series?.[0]?.type).toBe('heatmap');
    expect(option.xAxis?.type).toBe('category');
    expect(option.yAxis?.type).toBe('category');
    // 噪点(count=0)不渲染，数据点是栅格索引而非归一化坐标
    expect(option.series?.[0]?.data).toEqual([[3, 5, 2], [12, 30, 7]]);
  });
});
