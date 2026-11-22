import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { SettingsPage } from '../SettingsPage';
import { useAuthStore } from '@/store/auth';
import { fetchSettings, saveOtlpSettings, saveDuckDbSettings } from '@/api/settings';

const { fetchStatusMock } = vi.hoisted(() => ({ fetchStatusMock: vi.fn() }));
vi.mock('@/api/monitor', () => ({ fetchStatus: fetchStatusMock }));

// mock 成员面板 API（成员 tab 嵌入 MembersPanel）
vi.mock('@/api/users', () => ({
  fetchUsers: vi.fn().mockResolvedValue({ page: 1, pageSize: 20, count: 0, data: [] }),
  createUser: vi.fn(),
  deleteUser: vi.fn(),
  resetUserPassword: vi.fn(),
  updateUserRole: vi.fn(),
}));

// mock 改密 API（MembersPanel 引入 changePassword）
vi.mock('@/api/auth', () => ({
  changePassword: vi.fn(),
}));

// mock 系统设置 API
const { saveS3Mock, saveRetentionMock, saveCompressionMock, testS3Mock } = vi.hoisted(() => ({
  saveS3Mock: vi.fn(),
  saveRetentionMock: vi.fn(),
  saveCompressionMock: vi.fn(),
  testS3Mock: vi.fn(),
}));
vi.mock('@/api/settings', () => ({
  S3_SECRET_MASK: '******',
  fetchNotificationChannels: vi.fn().mockResolvedValue([]),
  saveNotificationChannel: vi.fn(),
  deleteNotificationChannel: vi.fn(),
  testNotificationChannel: vi.fn(),
  saveDuckDbSettings: vi.fn().mockResolvedValue({ memoryLimit: '2GB', effectiveMemoryLimit: '1.8 GiB' }),
  fetchSettings: vi.fn().mockResolvedValue({
    duckDb: { memoryLimit: '512MB', effectiveMemoryLimit: '488.2 MiB' },
    s3: { endpoint: 'http://minio:9000', bucket: 'sdata-cold', prefix: 'sdata/archive', accessKey: 'minioroot', secretKey: '******', hasSecret: true },
    retention: {
      logs: { hotDays: 30, coldDays: 90 },
      traces: { hotDays: 14, coldDays: 60 },
      metrics: { hotDays: 90, coldDays: 180 },
      iot: { hotDays: 365, coldDays: 0 },
      rum: { hotDays: 30, coldDays: 90 },
    },
    compression: { archiveParquetCodec: 'snappy' },
    otlp: { trace: '', metrics: '', protocol: 'grpc' },
    ingest: { requireAuth: true, bufferCapacity: 100000, batchSize: 5000, flushIntervalMs: 200 },
  }),
  saveS3Settings: saveS3Mock.mockResolvedValue({ endpoint: 'http://minio:9000', bucket: 'sdata-cold', prefix: '', accessKey: 'minioroot', secretKey: '******', hasSecret: true }),
  saveRetentionSettings: saveRetentionMock.mockImplementation(async (body) => body),
  saveCompressionSettings: saveCompressionMock.mockResolvedValue({ archiveParquetCodec: 'zstd' }),
  testS3Connection: testS3Mock.mockResolvedValue({ success: true, message: '连接成功，测试对象已写入' }),
  saveOtlpSettings: vi.fn().mockResolvedValue({ trace: '', metrics: '', protocol: 'grpc' }),
}));

function renderPage() {
  // MemoryRouter 提供 useSearchParams 上下文
  return render(<MemoryRouter><SettingsPage /></MemoryRouter>);
}

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
});

describe('SettingsPage', () => {
  it.each([undefined, null])('handles missing DuckDB settings from an older backend (%s) and recovers on retry', async (duckDb) => {
    const settings = await fetchSettings();
    vi.mocked(fetchSettings).mockResolvedValueOnce({ ...settings, duckDb });
    render(<MemoryRouter initialEntries={['/settings?tab=duckdb']}><SettingsPage /></MemoryRouter>);
    expect(await screen.findByRole('alert')).toHaveTextContent(/更新并重启后端|Update and restart the backend/);
    expect(screen.queryByLabelText(/内存限制|Memory limit/)).toBeNull();
    expect(saveDuckDbSettings).not.toHaveBeenCalled();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    expect(await screen.findByPlaceholderText('http://minio:9000')).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'DuckDB' }));
    fireEvent.click(screen.getByRole('button', { name: /重试|Retry/ }));
    expect(await screen.findByLabelText(/内存限制|Memory limit/)).toHaveValue('512MB');
    expect(screen.queryByTestId('duckdb-unavailable')).toBeNull();
  });

  it('increases DuckDB memory and keeps the saved value across tabs', async () => {
    render(<MemoryRouter initialEntries={['/settings?tab=duckdb']}><SettingsPage /></MemoryRouter>);
    const input = await screen.findByLabelText(/内存限制|Memory limit/);
    expect(input).toHaveValue('512MB');
    expect(screen.getByText('488.2 MiB')).toBeTruthy();
    fireEvent.change(input, { target: { value: '2GB' } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(saveDuckDbSettings).toHaveBeenCalledWith({ memoryLimit: '2GB' }));
    expect(await screen.findByText('1.8 GiB')).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /成员|Members/ }));
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'DuckDB' }));
    expect(await screen.findByLabelText(/内存限制|Memory limit/)).toHaveValue('2GB');
  });

  it.each(['', '0GB', '-2GB', 'unlimited', '2TB', '9999999999999999GB'])('rejects invalid DuckDB capacity %j', async (value) => {
    render(<MemoryRouter initialEntries={['/settings?tab=duckdb']}><SettingsPage /></MemoryRouter>);
    fireEvent.change(await screen.findByLabelText(/内存限制|Memory limit/), { target: { value } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(saveDuckDbSettings).not.toHaveBeenCalled();
  });

  it('preserves the DuckDB draft when saving fails', async () => {
    vi.mocked(saveDuckDbSettings).mockRejectedValueOnce(new Error('unavailable'));
    render(<MemoryRouter initialEntries={['/settings?tab=duckdb']}><SettingsPage /></MemoryRouter>);
    const input = await screen.findByLabelText(/内存限制|Memory limit/);
    fireEvent.change(input, { target: { value: '4GiB' } });
    const save = screen.getByRole('button', { name: /^(保\s*存|Save)$/ });
    fireEvent.click(save);
    await waitFor(() => expect(saveDuckDbSettings).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(save).not.toBeDisabled());
    expect(input).toHaveValue('4GiB');
    expect(screen.getByText('488.2 MiB')).toBeTruthy();
  });

  it('falls back to members for a non-admin DuckDB deep link', async () => {
    useAuthStore.setState({ token: 'test-token', username: 'reader', role: 'readonly' });
    render(<MemoryRouter initialEntries={['/settings?tab=duckdb']}><SettingsPage /></MemoryRouter>);
    expect(await screen.findByRole('tab', { name: /成员|Members/ })).toHaveAttribute('data-state', 'active');
    expect(screen.queryByRole('tab', { name: 'DuckDB' })).toBeNull();
    expect(fetchSettings).not.toHaveBeenCalled();
  });

  it('masks saved OTLP headers and confirms independent removal', async () => {
    const settings = await fetchSettings();
    vi.mocked(fetchSettings).mockResolvedValueOnce({ ...settings, otlp: { ...settings.otlp, traceHeaders: '******', metricsHeaders: '******' } });
    vi.mocked(saveOtlpSettings).mockResolvedValueOnce({ trace: '', metrics: '', protocol: 'grpc', traceHeaders: '******', metricsHeaders: '******' });
    render(<MemoryRouter initialEntries={['/settings?tab=otlp']}><SettingsPage /></MemoryRouter>);
    await screen.findByText(/外部 OTLP（可选）|External OTLP \(optional\)/);
    const inputs = screen.getAllByPlaceholderText('authorization=Bearer token,x-api-key=secret');
    expect(inputs[0]).toHaveValue('******');
    expect(inputs[0]).toHaveAttribute('type', 'password');
    fireEvent.change(inputs[0], { target: { value: 'authorization=Bearer test-token' } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(inputs[0]).toHaveValue('******'));
    expect(saveOtlpSettings).toHaveBeenCalledWith(expect.objectContaining({ traceHeaders: 'authorization=Bearer test-token', metricsHeaders: '******' }));
    fireEvent.click(screen.getAllByRole('button', { name: /清空请求头|Clear headers/ })[0]);
    fireEvent.click(screen.getByRole('button', { name: /^(取消|Cancel)$/ }));
    expect(inputs[0]).toHaveValue('******');
    fireEvent.click(screen.getAllByRole('button', { name: /清空请求头|Clear headers/ })[0]);
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /清空请求头|Clear headers/ }));
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(saveOtlpSettings).toHaveBeenLastCalledWith(expect.objectContaining({ traceHeaders: '', clearTraceHeaders: true, metricsHeaders: '******' })));
  });

  it('omits the local telemetry banner while saving and clearing external endpoints', async () => {
    render(<MemoryRouter initialEntries={['/settings?tab=otlp']}><SettingsPage /></MemoryRouter>);
    await screen.findByText(/外部 OTLP（可选）|External OTLP \(optional\)/);
    expect(screen.queryByTestId('local-telemetry')).toBeNull();
    expect(screen.queryByText(/本机遥测 · 始终启用|Local telemetry · Always enabled/)).toBeNull();
    const inputs = screen.getAllByPlaceholderText('http://localhost:4317');
    fireEvent.change(inputs[0], { target: { value: 'http://collector:4317' } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(saveOtlpSettings).toHaveBeenCalledWith(expect.objectContaining({ trace: 'http://collector:4317', metrics: '', protocol: 'grpc' })));
    fireEvent.change(inputs[0], { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(saveOtlpSettings).toHaveBeenLastCalledWith(expect.objectContaining({ trace: '', metrics: '', protocol: 'grpc' })));
    expect(screen.queryByTestId('local-telemetry')).toBeNull();
  });

  it('opens notification channels from an admin deep link', async () => {
    render(<MemoryRouter initialEntries={['/settings?tab=notifications']}><SettingsPage /></MemoryRouter>);
    expect(await screen.findByText('Telegram Bot')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^刷新$|^Refresh$/ })).toBeNull();
    expect(screen.getByRole('tab', { name: /通知渠道|Notification channels/ })).toHaveAttribute('data-state', 'active');
  });
  /// 管理员：顶部 tab 渲染 + tab 切换显示设置卡片（成员/S3）
  it('renders settings tabs and cards for admin', async () => {
    renderPage();

    // 成员 tab 默认激活，服务状态仅在 Overview 展示
    expect(await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ })).toBeTruthy();
    expect(screen.getByRole('tab', { name: /成员|Members/ })).toHaveAttribute('data-state', 'active');
    expect(screen.queryByText(/服务状态|Service status/)).toBeNull();
    expect(fetchStatusMock).not.toHaveBeenCalled();
    expect(screen.getByRole('tab', { name: /数据保留|Data Retention/ })).toBeTruthy();
    expect(screen.getByRole('tab', { name: /数据压缩|Data Compression/ })).toBeTruthy();
    expect(screen.getByRole('tab', { name: /自身遥测|Telemetry Export/ })).toBeTruthy();
    expect(screen.queryByRole('tab', { name: /摄取缓冲|Ingest Buffer/ })).toBeNull();
    expect(screen.queryByText(/摄取缓冲参数（只读）|Ingest Buffer Parameters/)).toBeNull();

    // 切到成员 tab：成员面板渲染（管理员可见新建用户入口）
    fireEvent.mouseDown(screen.getByRole('tab', { name: /成员|Members/ }));
    expect(await screen.findByRole('button', { name: /新建用户|Create User/ })).toBeTruthy();

    // 切到 S3 tab：卡片渲染 + SecretKey 掩码返回
    fireEvent.mouseDown(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    expect(await screen.findByText(/冷存储 S3（冷备）|Cold Storage S3 \(Archive\)/)).toBeTruthy();
    expect((await screen.findAllByPlaceholderText(/\*\*\*\*\*\*/)).length).toBeGreaterThan(0);
  });

  /// 非管理员：只有成员 tab，不加载管理员设置
  it('hides admin tabs for non-admin', async () => {
    useAuthStore.setState({ token: 'test-token', username: 'reader', role: 'readonly' });
    renderPage();

    expect(await screen.findByRole('tab', { name: /成员|Members/ })).toHaveAttribute('data-state', 'active');
    expect(screen.getAllByRole('tab')).toHaveLength(1);
    expect(screen.queryByText(/服务状态|Service status/)).toBeNull();
    expect(fetchStatusMock).not.toHaveBeenCalled();
    expect(screen.queryByRole('tab', { name: /冷存储 S3|Cold Storage S3/ })).toBeNull();
    expect(screen.queryByRole('tab', { name: /数据保留|Data Retention/ })).toBeNull();
    expect(screen.queryByRole('tab', { name: /通知渠道|Notification channels/ })).toBeNull();
  });

  it.each(['status', 'ingest', 'unknown'])('falls back to members for removed or invalid tab %s', async (tab) => {
    render(<MemoryRouter initialEntries={[`/settings?tab=${tab}`]}><SettingsPage /></MemoryRouter>);
    await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ });
    expect(screen.getByRole('tab', { name: /成员|Members/ })).toHaveAttribute('data-state', 'active');
    expect(screen.queryByText(/服务状态|Service status/)).toBeNull();
    expect(fetchStatusMock).not.toHaveBeenCalled();
  });

  it('keeps admin tabs and the selected deep link while settings are loading', async () => {
    const settings = await fetchSettings();
    let resolveSettings!: (value: typeof settings) => void;
    vi.mocked(fetchSettings).mockImplementationOnce(() => new Promise((resolve) => { resolveSettings = resolve; }));
    render(<MemoryRouter initialEntries={['/settings?tab=s3']}><SettingsPage /></MemoryRouter>);

    expect(screen.getAllByRole('tab')).toHaveLength(9);
    expect(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ })).toHaveAttribute('data-state', 'active');
    expect(screen.getByRole('status')).toBeTruthy();
    resolveSettings(settings);
    expect(await screen.findByPlaceholderText('http://minio:9000')).toBeTruthy();
  });

  it('keeps admin tabs on settings failure and allows retry', async () => {
    vi.mocked(fetchSettings).mockRejectedValueOnce(new Error('unavailable'));
    render(<MemoryRouter initialEntries={['/settings?tab=s3']}><SettingsPage /></MemoryRouter>);

    expect(await screen.findByRole('alert')).toHaveTextContent(/设置加载失败|Unable to load settings/);
    expect(screen.getAllByRole('tab')).toHaveLength(9);
    expect(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ })).toHaveAttribute('data-state', 'active');
    fireEvent.mouseDown(screen.getByRole('tab', { name: /通知渠道|Notification channels/ }));
    expect(await screen.findByText('Telegram Bot')).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    fireEvent.click(screen.getByRole('button', { name: /重试|Retry/ }));
    expect(await screen.findByPlaceholderText('http://minio:9000')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  /// 保存 S3：先切到 S3 tab，掩码密钥原样提交（后端沿用已存值）
  it('saves s3 settings with masked secret', async () => {
    renderPage();

    fireEvent.mouseDown(await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    const saveButtons = await screen.findAllByRole('button', { name: /保\s*存|Save/ });
    await fireEvent.click(saveButtons[0]);
    await waitFor(() => expect(saveS3Mock).toHaveBeenCalledTimes(1));
    const payload = saveS3Mock.mock.calls[0][0];
    expect(payload.endpoint).toBe('http://minio:9000');
    expect(payload.secretKey).toBe(''); // 掩码 → 空（后端沿用已存密钥）
    expect(payload.bucket).toBe('sdata-cold');
  });

  /// 测试连接：切到 S3 tab 后调用测试接口
  it('tests s3 connection', async () => {
    renderPage();

    fireEvent.mouseDown(await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    const testButton = await screen.findByRole('button', { name: /测试连接|Test Connection/ });
    fireEvent.click(testButton);
    await waitFor(() => expect(testS3Mock).toHaveBeenCalledTimes(1));
  });

  it('saves one-day archive policy and shares it with the retention tab', async () => {
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    expect(screen.queryByRole('button', { name: /设为 1 天|Set enabled cold tiers/ })).toBeNull();
    for (const domain of ['logs', 'traces', 'metrics', 'rum']) {
      fireEvent.change(screen.getByRole('spinbutton', { name: new RegExp(`${domain} (转冷|Local days)`) }), { target: { value: '1' } });
    }
    expect(screen.getByRole('spinbutton', { name: /logs (转冷|Local days)/ })).toHaveValue(1);
    const buttons = screen.getAllByRole('button', { name: /^(保\s*存|Save)$/ });
    fireEvent.click(buttons[1]);
    await waitFor(() => expect(saveRetentionMock).toHaveBeenCalledTimes(1));
    const payload = saveRetentionMock.mock.calls[0][0];
    expect(payload.logs).toEqual({ hotDays: 1, coldDays: 90 });
    expect(payload.traces.hotDays).toBe(1);
    expect(payload.metrics.hotDays).toBe(1);
    expect(payload.iot).toEqual({ hotDays: 365, coldDays: 0 });
    expect(payload.rum).toEqual({ hotDays: 1, coldDays: 90 });
    fireEvent.mouseDown(screen.getByRole('tab', { name: /数据保留|Data Retention/ }));
    expect(await screen.findByRole('spinbutton', { name: /logs (冷存|Cold Days)/ })).toHaveValue(90);
    expect(screen.queryByRole('spinbutton', { name: /logs (转冷|Local days)/ })).toBeNull();
    fireEvent.change(screen.getByRole('spinbutton', { name: /logs (冷存|Cold Days)/ }), { target: { value: '120' } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(saveRetentionMock).toHaveBeenCalledTimes(2));
    expect(saveRetentionMock.mock.calls[1][0]).toEqual({ ...payload, logs: { hotDays: 1, coldDays: 120 } });
    fireEvent.click(screen.getByRole('button', { name: /前往 S3|Manage archive timing/ }));
    expect(await screen.findByRole('spinbutton', { name: /logs (转冷|Local days)/ })).toHaveValue(1);
    expect(screen.queryByRole('spinbutton', { name: /logs (冷存|Cold Days)/ })).toBeNull();
    fireEvent.change(screen.getByRole('spinbutton', { name: /logs (转冷|Local days)/ }), { target: { value: '2' } });
    fireEvent.click(screen.getAllByRole('button', { name: /^(保\s*存|Save)$/ })[1]);
    await waitFor(() => expect(saveRetentionMock).toHaveBeenCalledTimes(3));
    expect(saveRetentionMock.mock.calls[2][0].logs).toEqual({ hotDays: 2, coldDays: 120 });
  });

  it.each(['', '   '])('shows the complete local policy without a saved S3 endpoint %j', async (endpoint) => {
    const settings = await fetchSettings();
    vi.mocked(fetchSettings).mockResolvedValueOnce({ ...settings, s3: { ...settings.s3, endpoint } });
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /数据保留|Data Retention/ }));
    expect(await screen.findAllByRole('spinbutton')).toHaveLength(10);
    fireEvent.change(screen.getByRole('spinbutton', { name: /logs (转冷|Local days)/ }), { target: { value: '3' } });
    fireEvent.change(screen.getByRole('spinbutton', { name: /logs (冷存|Cold Days)/ }), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    await waitFor(() => expect(saveRetentionMock).toHaveBeenCalledTimes(1));
    expect(saveRetentionMock.mock.calls[0][0]).toEqual({ ...settings.retention, logs: { hotDays: 3, coldDays: 100 } });
    fireEvent.mouseDown(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    expect(screen.queryAllByRole('spinbutton')).toHaveLength(0);
  });

  it('updates policy visibility only after saving S3 and keeps the saved state across tabs', async () => {
    const settings = await fetchSettings();
    saveS3Mock.mockResolvedValueOnce({ ...settings.s3, endpoint: '' });
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    fireEvent.click(await screen.findByRole('switch'));
    expect(screen.getAllByRole('spinbutton')).toHaveLength(5);
    fireEvent.click(screen.getAllByRole('button', { name: /^(保\s*存|Save)$/ })[0]);
    await waitFor(() => expect(screen.queryAllByRole('spinbutton')).toHaveLength(0));
    fireEvent.mouseDown(screen.getByRole('tab', { name: /数据保留|Data Retention/ }));
    expect(await screen.findAllByRole('spinbutton')).toHaveLength(10);
    fireEvent.mouseDown(screen.getByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    expect(await screen.findByRole('switch')).not.toBeChecked();

    fireEvent.click(screen.getByRole('switch'));
    fireEvent.change(screen.getByPlaceholderText('http://minio:9000'), { target: { value: settings.s3.endpoint } });
    fireEvent.click(screen.getByRole('button', { name: /测试连接|Test Connection/ }));
    await waitFor(() => expect(testS3Mock).toHaveBeenCalledTimes(1));
    expect(screen.queryAllByRole('spinbutton')).toHaveLength(0);
    await waitFor(() => expect(screen.getByRole('button', { name: /^(保\s*存|Save)$/ })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /^(保\s*存|Save)$/ }));
    expect(await screen.findAllByRole('spinbutton')).toHaveLength(5);
    fireEvent.mouseDown(screen.getByRole('tab', { name: /数据保留|Data Retention/ }));
    expect(await screen.findAllByRole('spinbutton')).toHaveLength(5);
    expect(screen.queryByRole('spinbutton', { name: /logs (转冷|Local days)/ })).toBeNull();
  });

  it('reloads the persisted S3 state when saving fails after persistence', async () => {
    const settings = await fetchSettings();
    vi.mocked(fetchSettings).mockResolvedValueOnce(settings).mockResolvedValueOnce({
      ...settings, s3: { ...settings.s3, endpoint: '' },
    });
    saveS3Mock.mockRejectedValueOnce(new Error('secret rebuild failed'));
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /冷存储 S3|Cold Storage S3/ }));
    fireEvent.click(await screen.findByRole('switch'));
    fireEvent.click(screen.getAllByRole('button', { name: /^(保\s*存|Save)$/ })[0]);
    await waitFor(() => expect(screen.queryAllByRole('spinbutton')).toHaveLength(0));
    expect(fetchSettings).toHaveBeenCalledTimes(3);
    expect(screen.getByRole('switch')).not.toBeChecked();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /数据保留|Data Retention/ }));
    expect(await screen.findAllByRole('spinbutton')).toHaveLength(10);
  });

  /// 保存压缩编码：切到数据压缩 tab
  it('saves compression codec', async () => {
    renderPage();

    fireEvent.mouseDown(await screen.findByRole('tab', { name: /数据压缩|Data Compression/ }));
    await screen.findByText(/数据压缩（冷存档）|Data Compression \(Archive\)/);
    const card = screen.getByTestId('settings-compression-card');
    expect(within(card).getByRole('heading', { name: /适用范围与生效时间|Scope and timing/ })).toBeTruthy();
    expect(within(card).getByText(/不压缩 DuckDB 数据库文件|Does not compress the DuckDB database file/)).toHaveTextContent(/SQLite/);
    expect(within(card).getByText(/仅用于 S3|Only applies to cold-archive/)).toHaveTextContent(/data\/cold\//);
    expect(within(card).getByText(/保存后下一轮归档|The new codec applies/)).toHaveTextContent(/10/);
    expect(within(card).getByText(/已有归档文件不会重新压缩|Existing archives are not recompressed/)).toHaveTextContent(/未启用归档的数据不受影响|data with archiving disabled is unaffected/);
    expect(within(card).getByRole('heading', { name: /编码参考|Codec reference/ })).toBeTruthy();
    expect(within(card).getByText(/默认，偏重速度|Default, prioritizes speed/)).toBeTruthy();
    expect(within(card).getByText(/^不压缩$|^No compression$/)).toBeTruthy();
    const selects = within(card).getAllByRole('combobox');
    expect(selects.length).toBeGreaterThan(0);
    const saveButtons = within(card).getAllByRole('button', { name: /保\s*存|Save/ });
    fireEvent.click(saveButtons[0]);
    await waitFor(() => expect(saveCompressionMock).toHaveBeenCalledTimes(1));
  });
});
