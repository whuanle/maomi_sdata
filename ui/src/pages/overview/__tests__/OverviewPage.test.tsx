import { act, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import '@/i18n';
import i18n from '@/i18n';
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="mock-chart" /> }));
import { OverviewPage } from '../OverviewPage';
import { useAuthStore } from '@/store/auth';
import { fetchOverview, fetchStatus, fetchHealth } from '@/api/monitor';
import { queryLogs } from '@/api/logs';
import { queryTraces } from '@/api/traces';

vi.mock('@/api/monitor', () => ({
  fetchOverview: vi.fn(),
  fetchStatus: vi.fn(),
  fetchHealth: vi.fn(),
}));
vi.mock('@/api/logs', () => ({ queryLogs: vi.fn() }));
vi.mock('@/api/traces', () => ({ queryTraces: vi.fn() }));

const overview = {
  todayRows: { logs: 42, traces: 0, metrics: 7, iot: 0, rum_event: 3, rum_session: 1, rum_replay: 0 },
  tableCounts: { logs: 5, traces: 4, metrics: 3, iot: 2, rum_event: 2, rum_session: 2, rum_replay: 1 },
  channels: [
    { name: 'logs', processed: 100, dropped: 0, buffered: 9, healthy: true },
    { name: 'rum_event', processed: 10, dropped: 5, buffered: 9, healthy: false },
  ],
  storage: { hotDbBytes: 1024 * 1024 * 64, hotDbPath: 'data/sdata.duckdb', coldEnabled: false, memoryLimit: '512MB' },
  bufferedRecords: 9,
  mqConsumers: [
    { instanceId: 1, name: 'queue-logs', type: 'rabbitmq', enabled: true, state: 'consuming', persistedRecords: 12, lastPersistedAt: '2026-11-18T00:00:00Z' },
    { instanceId: 2, name: 'disabled-logs', type: 'kafka', enabled: false, state: 'disabled', persistedRecords: 0 },
    { instanceId: 3, name: 'pending-logs', type: 'pulsar', enabled: true, state: 'unknown', persistedRecords: 0 },
    { instanceId: 4, name: 'failed-logs', type: 'kafka', enabled: true, state: 'error', persistedRecords: 0, lastError: 'safe error' },
  ],
};
const status = {
  name: 'sdata', version: '1.0.0', uptimeSeconds: 60, processMemoryBytes: 1024,
  gcHeapBytes: 512, threadCount: 4, os: 'test-os',
  buffer: { buffered: { log: 9, trace: 9 }, processed: { log: 9 }, dropped: { log: 0 } },
  tableCounts: {}, todayRows: { logs: 42 }, duckDbMemoryLimit: '512MB',
};

describe('OverviewPage', () => {
  beforeEach(async () => {
    vi.resetAllMocks();
    await i18n.changeLanguage('zh-CN');
    useAuthStore.setState({ token: 'test-token', username: 'reader', role: 'readonly' });
    vi.mocked(fetchOverview).mockResolvedValue(structuredClone(overview));
    vi.mocked(fetchStatus).mockResolvedValue(structuredClone(status));
    vi.mocked(fetchHealth).mockResolvedValue({ status: 'ok' });
  });
  afterEach(() => { vi.useRealTimers(); });

  it('展示七域总量、共享队列与系统信息，不查询日志或链路样本', async () => {
    render(<OverviewPage />);
    await screen.findByText('test-os');
    expect(screen.getByText('53')).toBeInTheDocument();
    expect(screen.getByText('rum_session')).toBeInTheDocument();
    expect(screen.getByText('rum_replay')).toBeInTheDocument();
    expect(screen.getByText('有丢弃')).toBeInTheDocument();
    expect(screen.getByText('正常')).toBeInTheDocument();
    expect(screen.getByText('9')).toBeInTheDocument();
    expect(screen.getByText('1.0.0')).toBeInTheDocument();
    expect(screen.queryByText('Recent logs')).not.toBeInTheDocument();
    expect(queryLogs).not.toHaveBeenCalled();
    expect(queryTraces).not.toHaveBeenCalled();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('展示存储摘要、MQ 状态与安全错误提示', async () => {
    vi.mocked(fetchOverview).mockResolvedValue({ ...overview, storage: { ...overview.storage, coldEnabled: true } });
    render(<OverviewPage />);
    await screen.findByText('64.0 MB');
    expect(screen.getByText('已配置 · 连通性未验证')).toBeInTheDocument();
    expect(screen.getByText('data/sdata.duckdb')).toBeInTheDocument();
    const row = screen.getByText('queue-logs').closest('tr')!;
    expect(within(row).getByText('已有落库')).toBeInTheDocument();
    expect(within(row).getByText('12')).toBeInTheDocument();
    expect(screen.getByText('尚无运行快照')).toBeInTheDocument();
    expect(screen.getByText('消费异常，请联系管理员查看服务日志')).toBeInTheDocument();
    expect(screen.queryByText('safe error')).not.toBeInTheDocument();
  });

  it('未配置 S3 和队列时显示只读空态', async () => {
    vi.mocked(fetchOverview).mockResolvedValue({ ...overview, mqConsumers: [] });
    render(<OverviewPage />);
    await screen.findByText('未配置');
    expect(screen.getByText('尚未配置队列日志消费')).toBeInTheDocument();
  });

  it('首次请求失败显示未知而非零值或健康', async () => {
    vi.mocked(fetchOverview).mockRejectedValue(new Error('offline'));
    vi.mocked(fetchStatus).mockRejectedValue(new Error('offline'));
    vi.mocked(fetchHealth).mockRejectedValue(new Error('offline'));
    render(<OverviewPage />);
    await screen.findByRole('alert');
    expect(screen.getAllByText('未知').length).toBeGreaterThan(0);
    expect(screen.queryByText('服务存活')).not.toBeInTheDocument();
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('刷新失败保留旧数据并撤销存活标记', async () => {
    vi.useFakeTimers();
    render(<OverviewPage />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('服务存活')).toBeInTheDocument();
    vi.mocked(fetchHealth).mockRejectedValue(new Error('offline'));
    vi.mocked(fetchOverview).mockRejectedValue(new Error('offline'));
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(screen.getByRole('alert')).toHaveTextContent('上次成功快照');
    expect(screen.getByText('53')).toBeInTheDocument();
    expect(screen.queryByText('服务存活')).not.toBeInTheDocument();
  });

  it('支持英文文案', async () => {
    await i18n.changeLanguage('en-US');
    render(<OverviewPage />);
    await waitFor(() => expect(screen.getByText('Service alive')).toBeInTheDocument());
    expect(screen.getByText('Data statistics')).toBeInTheDocument();
    expect(screen.getByText('Not configured')).toBeInTheDocument();
  });
});
