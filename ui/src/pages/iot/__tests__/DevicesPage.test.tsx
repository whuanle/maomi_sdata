import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import '@/i18n';
// jsdom 无 canvas，mock echarts 封装组件
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="mock-chart" /> }));
import { DevicesPage } from '../DevicesPage';
import { deleteDevice, getDeviceLatest, queryDevices, queryTelemetry, saveDevice } from '@/api/iot';
import { useToastStore } from '@/design-system';
import { useAuthStore } from '@/store/auth';

// mock 设备域 API（页面从 '@/api/iot' 直接引入五个函数）
vi.mock('@/api/iot', () => ({
  queryDevices: vi.fn(),
  saveDevice: vi.fn(),
  deleteDevice: vi.fn(),
  queryTelemetry: vi.fn(),
  getDeviceLatest: vi.fn(),
}));

const devices = [
  { id: 1, deviceKey: 'sensor-01', name: '温度传感器', group: '车间A', tagsJson: '{}', enabled: true, createTime: '2026-01-01T00:00:00Z', updateTime: '2026-01-02T08:00:00Z' },
  { id: 2, deviceKey: 'sensor-02', name: '湿度传感器', group: '', tagsJson: '{}', enabled: false, createTime: '2026-01-03T00:00:00Z', updateTime: '2026-01-03T00:00:00Z' },
];

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
  useToastStore.setState({ items: [] });
  vi.mocked(queryDevices).mockResolvedValue({ page: 1, pageSize: 20, count: devices.length, data: devices });
  vi.mocked(saveDevice).mockResolvedValue({ value: 9 });
  vi.mocked(deleteDevice).mockResolvedValue({});
  vi.mocked(queryTelemetry).mockResolvedValue({
    series: [{ signal: 'temperature', labels: '', points: [{ timestampMs: 1_760_000_000_000, value: 25.5 }] }],
  });
  vi.mocked(getDeviceLatest).mockResolvedValue([
    { signal: 'temperature', numValue: 25.5, strValue: null, timestampMs: 1_760_000_000_000 },
    { signal: 'state', numValue: null, strValue: 'RUN', timestampMs: 1_760_000_100_000 },
  ]);
});

/** 打开 Popconfirm（jsdom 下 Radix Popover 触发器由 click 切换）. */
function openPopconfirm(trigger: HTMLElement) {
  fireEvent.click(trigger);
}

describe('DevicesPage', () => {
  it('渲染设备列表：分页参数/标签/审计列', async () => {
    render(<DevicesPage />);
    expect(await screen.findByText('sensor-01')).toBeInTheDocument();
    expect(screen.getByText('温度传感器')).toBeInTheDocument();
    expect(screen.getByText('车间A')).toBeInTheDocument();
    // 启用状态徽章：ON / OFF
    expect(screen.getByText('ON')).toBeInTheDocument();
    expect(screen.getByText('OFF')).toBeInTheDocument();
    // 服务端分页：首页加载 page=1 & pageSize=20，底部显示总数
    expect(queryDevices).toHaveBeenCalledWith({ page: 1, pageSize: 20, search: undefined });
    expect(screen.getByText('共 2 条')).toBeInTheDocument();
  });

  it('查询与新建同一工具条，分页只查询一次且查询回到第一页', async () => {
    vi.mocked(queryDevices).mockResolvedValue({ page: 1, pageSize: 20, count: 41, data: devices });
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    const query = screen.getByRole('button', { name: '查询' });
    const create = screen.getByRole('button', { name: '新建' });
    expect(query.parentElement).toBe(create.parentElement);
    expect(query.nextElementSibling).toBe(create);
    expect(screen.getByRole('button', { name: '接入认证' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '刷新' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'next page' }));
    await waitFor(() => expect(queryDevices).toHaveBeenCalledTimes(2));
    expect(vi.mocked(queryDevices).mock.lastCall?.[0].page).toBe(2);
    fireEvent.change(screen.getByTestId('device-search'), { target: { value: 'sensor' } });
    fireEvent.click(query);
    await waitFor(() => expect(queryDevices).toHaveBeenCalledTimes(3));
    expect(vi.mocked(queryDevices).mock.lastCall?.[0]).toEqual({ page: 1, pageSize: 20, search: 'sensor' });
  });

  it.each(['write', 'readonly'] as const)('非管理员 %s 隐藏接入认证', async role => {
    useAuthStore.setState({ role });
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    expect(screen.queryByRole('button', { name: '接入认证' })).not.toBeInTheDocument();
  });

  it('搜索框回车触发带 search 参数的查询', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.change(screen.getByTestId('device-search'), { target: { value: 'sensor' } });
    fireEvent.keyDown(screen.getByTestId('device-search'), { key: 'Enter' });
    await waitFor(() => {
      const calls = vi.mocked(queryDevices).mock.calls;
      expect(calls[calls.length - 1]?.[0]).toEqual({ page: 1, pageSize: 20, search: 'sensor' });
    });
  });

  it('只读角色无新建/编辑/删除入口，遥测入口保留', async () => {
    useAuthStore.setState({ token: 'test-token', username: 'viewer', role: 'readonly' });
    render(<DevicesPage />);
    expect(await screen.findByText('sensor-01')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /新建/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /编辑/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /删除/ })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /遥测曲线/ })).toHaveLength(2);
  });

  it('新建设备：表单校验通过后保存并刷新列表', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.click(screen.getByRole('button', { name: /新建/ }));
    await screen.findByLabelText(/设备标识/);
    fireEvent.change(screen.getByLabelText(/设备标识/), { target: { value: 'sensor-09' } });
    fireEvent.change(screen.getByLabelText(/设备名称/), { target: { value: '压力传感器' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => {
      expect(saveDevice).toHaveBeenCalledWith({ id: 0, deviceKey: 'sensor-09', name: '压力传感器', group: '', tagsJson: '{}', enabled: true });
    });
    // toast 提示 + 列表刷新
    expect(useToastStore.getState().items.some((item) => item.content === '保存')).toBe(true);
    expect(vi.mocked(queryDevices).mock.calls.length).toBeGreaterThan(1);
  });

  it('新建设备：设备标识非法时不提交', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.click(screen.getByRole('button', { name: /新建/ }));
    await screen.findByLabelText(/设备标识/);
    fireEvent.change(screen.getByLabelText(/设备标识/), { target: { value: '非法 key!' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    // 校验错误文案出现且未发起保存
    expect(await screen.findByText('a-zA-Z0-9_ - : .')).toBeInTheDocument();
    expect(saveDevice).not.toHaveBeenCalled();
  });

  it('编辑设备：表单回填且设备标识只读', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.click(screen.getAllByRole('button', { name: /编辑/ })[0]);
    expect(await screen.findByText('编辑 · sensor-01')).toBeInTheDocument();
    const keyInput = screen.getByLabelText(/设备标识/) as HTMLInputElement;
    expect(keyInput).toBeDisabled();
    expect(keyInput.value).toBe('sensor-01');
    expect((screen.getByLabelText(/设备名称/) as HTMLInputElement).value).toBe('温度传感器');
    expect((screen.getByLabelText(/标签/) as HTMLTextAreaElement).value).toBe('{}');
  });

  it('删除设备：经 Popconfirm 二次确认后调用删除', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    openPopconfirm(screen.getAllByRole('button', { name: '删除' })[0]);
    const title = await screen.findByText('确认删除该记录？');
    const box = title.parentElement as HTMLElement;
    fireEvent.click(within(box).getByRole('button', { name: '删除' }));
    await waitFor(() => {
      expect(deleteDevice).toHaveBeenCalledWith(1);
    });
    await waitFor(() => {
      expect(queryDevices).toHaveBeenCalledTimes(2);
    });
  });

  it('遥测抽屉：推送主题清单展示完整主题串与最后推送时间，图表默认渲染', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.click(screen.getAllByRole('button', { name: /遥测曲线/ })[0]);
    expect(await screen.findByText('推送主题')).toBeInTheDocument();
    expect(queryTelemetry).toHaveBeenCalledWith(expect.objectContaining({ deviceKey: 'sensor-01', stepSeconds: 60 }));
    // 完整主题串（deviceKey + 信号即 MQTT 子主题）
    expect(screen.getByText('iot/sensor-01/temperature')).toBeInTheDocument();
    expect(screen.getByText('iot/sensor-01/state')).toBeInTheDocument();
    // 字符串信号标注状态量且不可点击上图
    expect(screen.getByText('状态量')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /iot\/sensor-01\/state/ })).not.toBeInTheDocument();
    // 数值主题行默认选中上图
    expect(screen.getByRole('button', { name: /iot\/sensor-01\/temperature/ })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
    // 抽屉标题展示设备标识（表格行中已有同文案，断言存在等宽 code 样式元素）
    expect(screen.getAllByText('sensor-01').some((el) => el.tagName === 'CODE')).toBe(true);
  });

  it('遥测抽屉：点击主题行切换曲线选中态，客户端过滤不重新查询', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.click(screen.getAllByRole('button', { name: /遥测曲线/ })[0]);
    const tempRow = await screen.findByRole('button', { name: /iot\/sensor-01\/temperature/ });
    expect(tempRow).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(tempRow);
    expect(tempRow).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(tempRow);
    expect(tempRow).toHaveAttribute('aria-pressed', 'true');
    // 仅打开抽屉时查询一次，切换选中为客户端过滤
    expect(queryTelemetry).toHaveBeenCalledTimes(1);
  });

  it('遥测抽屉：切换时间窗以对应步长重新查询', async () => {
    render(<DevicesPage />);
    await screen.findByText('sensor-01');
    fireEvent.click(screen.getAllByRole('button', { name: /遥测曲线/ })[0]);
    await screen.findByText('推送主题');
    expect(queryTelemetry).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: '近 1 天' }));
    await waitFor(() => expect(queryTelemetry).toHaveBeenCalledTimes(2));
    expect(vi.mocked(queryTelemetry).mock.lastCall?.[0]).toEqual(expect.objectContaining({ deviceKey: 'sensor-01', stepSeconds: 300 }));
    // 选中态落到新窗口按钮上
    expect(screen.getByRole('button', { name: '近 1 天' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: '近 1 小时' })).toHaveAttribute('aria-pressed', 'false');
  });
});
