import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { LogConsumptionPage } from '../LogConsumptionPage';
import { useAuthStore } from '@/store/auth';
import { fetchNotificationChannels, fetchMqInstances, parseMqPreview } from '@/api/settings';
import { fetchMqConsumption, retryMqConsumer } from '@/api/monitor';
import type { MqInstance, MqParserOptions } from '@/api/types';

// mock MQ 实例 API（多实例 + 解析规则预览）
const { saveMock } = vi.hoisted(() => ({
  saveMock: vi.fn(),
}));
vi.mock('@/api/settings', () => ({
  fetchMqInstances: vi.fn().mockResolvedValue([
    {
      id: 1,
      name: 'prod-kafka',
      type: 'kafka',
      enabled: true,
      bootstrapServers: 'broker1:9092',
      groupId: 'sdata-logs',
      autoOffsetReset: 'earliest',
      uri: '',
      serviceUrl: '',
      subscriptionName: 'sdata-logs',
      subscriptionType: 'Shared',
      topics: [{ name: 'app-logs', format: 'raw', service: 'order-api', severity: 'INFO', parser: null }],
    },
    {
      id: 2,
      name: 'legacy-rabbit',
      type: 'rabbitmq',
      enabled: false,
      bootstrapServers: '',
      groupId: 'sdata-logs',
      autoOffsetReset: 'earliest',
      uri: 'amqp://guest:guest@localhost:5672/',
      serviceUrl: '',
      subscriptionName: 'sdata-logs',
      subscriptionType: 'Shared',
      topics: [],
    },
  ]),
  fetchNotificationChannels: vi.fn().mockResolvedValue([]),
  saveMqInstances: saveMock.mockResolvedValue(undefined),
  parseMqPreview: vi.fn().mockResolvedValue({ success: true, error: null, records: [] }),
}));

vi.mock('@/api/monitor', async (importOriginal) => ({
  ...await importOriginal<typeof import('@/api/monitor')>(),
  retryMqConsumer: vi.fn().mockResolvedValue(true),
  fetchMqConsumption: vi.fn().mockResolvedValue({ sampledAt: '2026-11-19T16:00:00Z', windowSeconds: 60, instances: [{ instanceId: 1, state: 'consuming', persistedRecords: 42, lastPersistedAt: '2026-11-17T16:00:00Z', topics: [] }] }),
}));

function renderPage() {
  // MemoryRouter 提供 router 上下文（toast 为全局命令式入口，无需 Provider）
  return render(<MemoryRouter><LogConsumptionPage /></MemoryRouter>);
}

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
});

describe('LogConsumptionPage', () => {
  /// 管理员：实例卡片渲染（类型 Tag + 连接摘要 + topic 列表）
  it('renders instance cards for admin', async () => {
    renderPage();

    expect(await screen.findByText('prod-kafka')).toBeTruthy();
    expect(screen.getByText('legacy-rabbit')).toBeTruthy();
    expect(screen.queryByText('broker1:9092')).toBeNull();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    expect(screen.getByText('broker1:9092')).toBeTruthy();
    expect(screen.getByText('sdata-logs')).toBeTruthy();
    expect(screen.getByText('earliest')).toBeTruthy();
    expect(screen.getByText('app-logs')).toBeTruthy();
    expect(screen.getByRole('button', { name: /新建实例|New Instance/ })).toBeTruthy();
  });

  it('shows persisted runtime and masks connection credentials', async () => {
    renderPage();
    expect(await screen.findByText('42')).toBeTruthy();
    expect(screen.getAllByText(/本次会话已落库|Persisted this session/).length).toBeGreaterThan(0);
    fireEvent.mouseDown(screen.getByRole('tab', { name: /RabbitMQ\s*1/ }), { button: 0, ctrlKey: false });
    expect(screen.queryByText('amqp://guest:guest@localhost:5672/')).toBeNull();
    expect(screen.getByText('amqp://***@localhost:5672/')).toBeTruthy();
  });

  it('filters by queue tabs and shows counts including disabled instances', async () => {
    renderPage();
    const all = await screen.findByRole('tab', { name: /Status\s*2/ });
    expect(screen.getByRole('tab', { name: /Kafka\s*1/ })).toBeTruthy();
    const rabbit = screen.getByRole('tab', { name: /RabbitMQ\s*1/ });
    const pulsar = screen.getByRole('tab', { name: /Pulsar\s*0/ });
    fireEvent.mouseDown(rabbit, { button: 0, ctrlKey: false });
    expect(screen.queryByText('prod-kafka')).toBeNull();
    expect(screen.getByText('legacy-rabbit')).toBeTruthy();
    expect(rabbit.getAttribute('aria-selected')).toBe('true');
    fireEvent.mouseDown(pulsar, { button: 0, ctrlKey: false });
    expect(screen.queryByText('legacy-rabbit')).toBeNull();
    expect(screen.getByRole('tabpanel')).toBeTruthy();
    fireEvent.mouseDown(all, { button: 0, ctrlKey: false });
    expect(screen.getByText('prod-kafka')).toBeTruthy();
    expect(screen.getByText('legacy-rabbit')).toBeTruthy();
  });

  it('defaults a new instance to the selected queue type', async () => {
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /RabbitMQ\s*1/ }), { button: 0, ctrlKey: false });
    fireEvent.click(screen.getByRole('button', { name: /新建实例|New Instance/ }));
    fireEvent.change(await screen.findByPlaceholderText(/实例名称|Instance Name/), { target: { value: 'new-rabbit' } });
    fireEvent.change(screen.getByPlaceholderText('amqp://guest:guest@localhost:5672/'), { target: { value: 'amqp://localhost:5672/' } });
    fireEvent.click(screen.getByRole('button', { name: /保\s*存|Save/ }));
    await waitFor(() => expect(saveMock).toHaveBeenCalledTimes(1));
    expect(saveMock.mock.calls[0][0][2].type).toBe('rabbitmq');
  });

  /// 管理员：停用开关直接保存实例列表
  it('persists toggle switch', async () => {
    renderPage();

    fireEvent.mouseDown(await screen.findByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    const switches = await screen.findAllByRole('switch');
    fireEvent.click(switches[0]);
    await waitFor(() => expect(saveMock).toHaveBeenCalledTimes(1));
    const payload = saveMock.mock.calls[0][0];
    expect(payload.find((x: { id: number }) => x.id === 1).enabled).toBe(false);
  });

  /// 管理员：打开新建实例抽屉，填名称后保存（新实例 id=0 追加入列表）
  it('opens drawer and saves new instance', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /新建实例|New Instance/ }));
    const nameInput = await screen.findByPlaceholderText(/实例名称|Instance Name/);
    fireEvent.change(nameInput, { target: { value: 'new-kafka' } });
    fireEvent.change(screen.getByPlaceholderText('broker1:9092,broker2:9092'), { target: { value: 'broker9:9092' } });

    fireEvent.click(screen.getByRole('button', { name: /保\s*存|Save/ }));
    await waitFor(() => expect(saveMock).toHaveBeenCalledTimes(1));
    const payload = saveMock.mock.calls[0][0];
    expect(payload.length).toBe(3);
    expect(payload[2].id).toBe(0);
    expect(payload[2].type).toBe('kafka');
    expect(payload[2].name).toBe('new-kafka');
  });

  it('shows effective parser configuration after each queue name', async () => {
    const base = (await fetchMqInstances())[0];
    const parser: MqParserOptions = { timestampField: 'data.time', timestampFormat: '', severityField: '', serviceField: '', bodyField: 'data.message', traceIdField: '', spanIdField: '', pattern: '(?<msg>.*)', delimiter: '|', keepUnmapped: false };
    vi.mocked(fetchMqInstances).mockResolvedValueOnce([{ ...base, topics: [
      { name: 'json-queue', format: 'json', service: '', severity: 'WARN', parser },
      { name: 'csv-queue', format: 'csv', service: '', severity: 'INFO', parser: null },
      { name: 'regex-queue', format: 'regex', service: '', severity: 'INFO', parser },
      { name: 'raw-queue', format: 'raw', service: '', severity: 'INFO', parser },
    ] }]);
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    const json = (await screen.findByText('json-queue')).parentElement!;
    expect(within(json).getByText('data.message')).toBeTruthy();
    expect(within(json).getByText('WARN')).toBeTruthy();
    expect(within(json).getByText(/未指定|Unspecified/)).toBeTruthy();
    expect(within(json).queryByText('(?<msg>.*)')).toBeNull();
    const csv = screen.getByText('csv-queue').parentElement!;
    expect(within(csv).getByText('","')).toBeTruthy();
    expect(within(csv).getByText(/默认字段映射|Default field mapping/)).toBeTruthy();
    expect(within(screen.getByText('regex-queue').parentElement!).getByText('(?<msg>.*)')).toBeTruthy();
    expect(within(screen.getByText('raw-queue').parentElement!).queryByText('data.message')).toBeNull();
  });

  it('cancels an edited draft without changing the instance and preserves config when saving', async () => {
    const base = (await fetchMqInstances())[0];
    const configured: MqInstance = { ...base, saslPassword: '********', securityProtocol: 'SaslSsl', topics: [{ ...base.topics[0], matchMode: 'prefix' }] };
    vi.mocked(fetchMqInstances).mockResolvedValueOnce([configured]);
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole('button', { name: /编辑实例|Edit Instance/ }));
    fireEvent.change(screen.getByPlaceholderText(/实例名称|Instance Name/), { target: { value: 'discarded' } });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /取消|Cancel/ }));
    expect(saveMock).not.toHaveBeenCalled();
    expect(screen.getByText('prod-kafka')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /编辑实例|Edit Instance/ }));
    expect((screen.getByPlaceholderText(/实例名称|Instance Name/) as HTMLInputElement).value).toBe('prod-kafka');
    fireEvent.click(screen.getByRole('button', { name: /保存|Save/ }));
    await waitFor(() => expect(saveMock).toHaveBeenCalledTimes(1));
    expect(saveMock.mock.calls[0][0][0]).toEqual(configured);
  });

  it('keeps sample parsing available inside the queue section', async () => {
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    fireEvent.click((await screen.findAllByRole('button', { name: /编辑实例|Edit Instance/ }))[0]);
    const sample = screen.getByPlaceholderText(/样例日志|Sample log/);
    fireEvent.click(sample.closest('details')!.querySelector('summary')!);
    fireEvent.change(sample, { target: { value: 'sample message' } });
    fireEvent.click(screen.getByRole('button', { name: /测试解析|Test Parse/ }));
    await waitFor(() => expect(parseMqPreview).toHaveBeenCalledWith(expect.objectContaining({ format: 'raw', sample: 'sample message', parser: null })));
  });

  it('shows topic rates and parse warnings without connection details', async () => {
    vi.mocked(fetchMqConsumption).mockResolvedValueOnce({
      sampledAt: '2026-11-19T16:00:00Z', windowSeconds: 60,
      instances: [{ instanceId: 1, state: 'consuming', persistedRecords: 42, topics: [{
        name: 'app-logs', matchMode: 'exact', state: 'consuming', matchedTopics: 2,
        receivedMessages: 20, receivedBytes: 200, persistedRecords: 42,
        parseFailures: 3, blockedMessages: 0, confirmedMessages: 20, confirmationFailures: 1,
        messagesPerSecond: 1.25, recordsPerSecond: 2.5, bytesPerSecond: 100,
        stage: 'confirm', stageSince: '2026-11-19T15:59:00Z', pendingPersistence: 0, pendingConfirmation: 1,
        failures: [{ stage: 'parse', code: 'json_invalid', reason: 'JSON truncated at byte 141', at: '2026-11-19T15:58:00Z', occurrences: 3 }],
      }] }],
    });
    renderPage();
    expect((await screen.findAllByText(/存在解析失败|Parse failures recorded/)).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/1.25/).length).toBeGreaterThan(0);
    expect(screen.queryByText('broker1:9092')).toBeNull();
    expect(screen.queryByRole('switch')).toBeNull();
    expect(screen.getByText('60s')).toBeTruthy();
    for (const label of screen.getAllByText(/存在解析失败|Parse failures recorded/)) expect(label).toHaveClass('text-error');
    expect(screen.getByText(/最近失败|Recent failures/)).toHaveClass('text-error');
    fireEvent.click(screen.getByText(/最近失败|Recent failures/));
    expect(screen.getByText('JSON truncated at byte 141')).toBeVisible();
    expect(screen.queryByText(/跳过|Skipped/)).toBeNull();
    expect(screen.queryByRole('tab', { name: /All|全部/ })).toBeNull();
  });

  it('shows independent topic states and bounded rates in both views', async () => {
    const base = (await fetchMqInstances())[0];
    const names = ['active', 'idle', 'broken', 'starting', 'unknown'];
    vi.mocked(fetchMqInstances).mockResolvedValueOnce([{ ...base, topics: names.map(name => ({
      ...base.topics[0], name, matchMode: name === 'active' ? 'prefix' : 'exact',
    })) }]);
    const topic = {
      matchMode: 'exact', matchedTopics: 0, receivedMessages: 1, receivedBytes: 20,
      persistedRecords: 1, parseFailures: 0, blockedMessages: 0, confirmedMessages: 1,
      confirmationFailures: 0, messagesPerSecond: 0.0333333333, recordsPerSecond: 0.0666666666,
      bytesPerSecond: 37.733333333333334,
    };
    vi.mocked(fetchMqConsumption).mockResolvedValueOnce({
      sampledAt: '2026-11-19T16:00:00Z', windowSeconds: 60,
      instances: [{ instanceId: 1, state: 'starting', persistedRecords: 1, topics: [
        { ...topic, name: 'active', matchMode: 'prefix', matchedTopics: 3, state: 'consuming' },
        { ...topic, name: 'idle', state: 'idle', messagesPerSecond: 0, recordsPerSecond: 0, bytesPerSecond: 0 },
        { ...topic, name: 'broken', state: 'error', lastError: 'only broken failed' },
        { ...topic, name: 'starting', state: 'starting' },
      ] }],
    });
    renderPage();
    const active = (await screen.findByText('active*')).closest('tr')!;
    expect(within(active).getByText(/正在消费|Consuming/)).toBeTruthy();
    expect(within(active).getByText(/37\.7 B\/s/)).toBeTruthy();
    expect(within(screen.getByText('starting').closest('tr')!).queryByText(/37\.7 B\/s/)).toBeNull();
    expect(within(screen.getByText('unknown').closest('tr')!).getAllByText(/未知|Unknown/).length).toBeGreaterThan(0);
    fireEvent.mouseDown(screen.getByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    const row = (name: string) => screen.getByText(name).parentElement!.parentElement!;
    expect(within(row('active')).getByText(/正在消费|Consuming/)).toBeTruthy();
    expect(within(row('active')).getByText(/0\.03.*0\.07.*37\.7 B\/s/)).toBeTruthy();
    expect(within(row('active')).getByText(/匹配 3 个|3 matched/)).toBeTruthy();
    expect(within(row('idle')).getByText(/空闲|Idle/)).toBeTruthy();
    expect(within(row('idle')).getByText(/0 B\/s/)).toBeTruthy();
    expect(within(row('broken')).getByText('only broken failed')).toBeTruthy();
    expect(within(row('active')).queryByText('only broken failed')).toBeNull();
    expect(within(row('unknown')).getByText(/未知|Unknown/)).toBeTruthy();
    expect(screen.queryByText(/37\.733333/)).toBeNull();
  });

  it('shows an active writer failure as an alert', async () => {
    vi.mocked(fetchMqConsumption).mockResolvedValueOnce({
      sampledAt: '2026-11-20T08:26:00Z', windowSeconds: 60, instances: [],
      writerFailure: { code: 'storage_memory', reason: 'DuckDB 内存不足，批次保留重试', at: '2026-11-20T08:25:59Z', recoveredAt: null },
    });
    renderPage();
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveClass('text-error');
    expect(alert).toHaveTextContent('storage_memory');
    expect(alert).toHaveTextContent('DuckDB 内存不足');
  });

  it('hides recovered persistence failures entirely', async () => {
    vi.mocked(fetchMqConsumption).mockResolvedValueOnce({
      sampledAt: '2026-11-20T08:27:25Z', windowSeconds: 60, instances: [],
      writerFailure: { code: 'storage_memory', reason: 'DuckDB 内存不足，批次保留重试', at: '2026-11-20T08:25:59Z', recoveredAt: '2026-11-20T08:26:00Z' },
    });
    renderPage();
    await screen.findByText('prod-kafka');
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText('storage_memory')).toBeNull();
    expect(screen.queryByText('DuckDB 内存不足，批次保留重试')).toBeNull();
    expect(screen.queryByText(/持久化已恢复|Persistence recovered/)).toBeNull();
    expect(screen.queryByText(/最近失败|Recent failures/)).toBeNull();
  });

  it('reports unavailable status instead of showing healthy zero rates', async () => {
    vi.mocked(fetchMqConsumption).mockRejectedValueOnce(new Error('offline'));
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent(/状态刷新失败|Status refresh failed/);
    expect(screen.getByText('prod-kafka')).toBeTruthy();
  });

  it('queues an instance retry only after confirmation', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /立即重试实例|Retry instance now/ }));
    expect(retryMqConsumer).not.toHaveBeenCalled();
    expect(await screen.findByText(/重新连接该实例及其全部主题|Reconnect this instance/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^(重试|Retry)$/ }));
    await waitFor(() => expect(retryMqConsumer).toHaveBeenCalledWith(1));
  });

  it('saves selected failure channels and retry settings', async () => {
    vi.mocked(fetchNotificationChannels).mockResolvedValueOnce([{ id: '42', name: 'fake channel', type: 'webhook', enabled: true } as Awaited<ReturnType<typeof fetchNotificationChannels>>[number]]);
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('tab', { name: /Kafka\s*1/ }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole('button', { name: /编辑实例|Edit Instance/ }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'fake channel' }));
    fireEvent.click(screen.getByRole('switch', { name: /消费失败通知|Notify on consumer failures/ }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /保存|Save/ }));
    await waitFor(() => expect(saveMock).toHaveBeenCalledTimes(1));
    expect(saveMock.mock.calls[0][0][0]).toMatchObject({ alertsEnabled: true, notificationChannelIds: ['42'] });
  });

  /// 非管理员：不加载实例，仅展示管理员提示
  it('shows admin-only alert for non-admin', async () => {
    useAuthStore.setState({ token: 'test-token', username: 'reader', role: 'readonly' });
    renderPage();

    expect(await screen.findByText(/设置管理仅超级管理员可见|super admins only/)).toBeTruthy();
    expect(screen.queryByText('prod-kafka')).toBeNull();
  });
});
