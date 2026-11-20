import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { fetchOverview, fetchStatus, fetchHealth } from '@/api/monitor';
import type { OverviewResponse, StatusResponse } from '@/api/types';
import { StatCard } from '@/design-system';
import { formatBytes, formatDateTime, formatUptime } from '@/utils/format';
import { cn } from '@/utils/cn';
import { ResourcePanels } from './ResourcePanels';

/** 卡片容器（概览栅格通用：标题 + 内容）. */
function Panel({ title, children, className }: { title: string; children: React.ReactNode; className?: string }) {
  return (
    <section className={cn('flex min-w-0 flex-col rounded-lg border border-border bg-surface', className)}>
      <h3 className="px-4 pt-3 text-sm font-medium text-muted">{title}</h3>
      <div className="min-h-0 flex-1 px-4 pb-3">{children}</div>
    </section>
  );
}

function InfoRow({ label, value }: { label: string; value: React.ReactNode }) {
  return <div className="flex justify-between gap-4 border-b border-border/60 py-2 text-xs"><span className="shrink-0 text-muted">{label}</span><span className="min-w-0 break-all text-right font-mono">{value}</span></div>;
}

type Source = 'health' | 'status' | 'overview';

/** 系统运行总览：进程状态、七类数据统计、存储与队列消费. */
export function OverviewPage() {
  const { t } = useTranslation();
  const [status, setStatus] = useState<StatusResponse | null>(null);
  const [overview, setOverview] = useState<OverviewResponse | null>(null);
  const [healthy, setHealthy] = useState<boolean | null>(null);
  const [updated, setUpdated] = useState<Partial<Record<Source, string>>>({});
  const [failed, setFailed] = useState<Source[]>([]);
  const [loading, setLoading] = useState(true);
  const active = useRef(false);
  const refreshing = useRef(false);

  const refresh = useCallback(async () => {
    if (refreshing.current) return;
    refreshing.current = true;
    const results = await Promise.allSettled([fetchHealth(), fetchStatus(), fetchOverview()]);
    refreshing.current = false;
    if (!active.current) return;
    const [healthResult, statusResult, overviewResult] = results;
    const now = new Date().toISOString();
    const sources: Source[] = ['health', 'status', 'overview'];
    setFailed(sources.filter((_, index) => results[index].status === 'rejected'));
    setUpdated((previous) => {
      const next = { ...previous };
      sources.forEach((source, index) => { if (results[index].status === 'fulfilled') next[source] = now; });
      return next;
    });
    setHealthy(healthResult.status === 'fulfilled' ? healthResult.value.status === 'ok' : null);
    if (statusResult.status === 'fulfilled') setStatus(statusResult.value);
    if (overviewResult.status === 'fulfilled') setOverview(overviewResult.value);
    setLoading(false);
  }, []);

  useEffect(() => {
    active.current = true;
    void refresh();
    const timer = setInterval(() => void refresh(), 10_000);
    return () => { active.current = false; clearInterval(timer); };
  }, [refresh]);

  const unknown = t('overview.unknown');
  const total = overview ? Object.values(overview.todayRows).reduce((sum, count) => sum + count, 0) : unknown;
  const consumers = overview?.mqConsumers ?? [];
  const mqStates = ['starting', 'retrying', 'consuming', 'idle', 'persisting', 'error', 'stopped', 'disabled', 'unknown'];
  const stateLabel = (state: string) => t(`overview.mqState.${mqStates.includes(state) ? state : 'unknown'}`);

  return (
    <div className="flex flex-col gap-4 pb-4">
      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted">
        <span>{t('overview.refreshHint')}</span>
        <span>{loading ? t('overview.loading') : t('overview.updatedAt', {
          status: updated.status ? formatDateTime(updated.status) : unknown,
          overview: updated.overview ? formatDateTime(updated.overview) : unknown,
        })}</span>
      </div>
      {failed.length > 0 && <div role="alert" className="rounded-lg border border-warning/40 bg-warning-soft px-4 py-3 text-sm text-warning">
        {t('overview.refreshFailed', { sources: failed.map((source) => t(`overview.source.${source}`)).join(' / ') })}
      </div>}

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5">
        <StatCard title={t('settings.status')} value={healthy === null ? unknown : t(healthy ? 'overview.alive' : 'overview.unhealthy')} valueClassName={healthy === true ? 'text-success' : 'text-warning'} hint={t('overview.livenessHint')} />
        <StatCard title={t('settings.uptime')} value={status ? formatUptime(status.uptimeSeconds) : unknown} />
        <StatCard title={t('overview.processMemory')} value={status ? formatBytes(status.processMemoryBytes) : unknown} />
        <StatCard title={t('overview.todayRows')} value={total} hint={t('overview.todayHint')} />
        <StatCard title={t('overview.sharedBuffer')} value={overview?.bufferedRecords ?? unknown} hint={t('overview.sharedBufferHint')} />
      </div>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Panel title={t('overview.system')}>
          <InfoRow label={t('overview.serviceName')} value={status?.name ?? unknown} />
          <InfoRow label={t('overview.version')} value={status?.version ?? unknown} />
          <InfoRow label={t('overview.os')} value={status?.os ?? unknown} />
          <InfoRow label={t('settings.threads')} value={status?.threadCount ?? unknown} />
          <InfoRow label={t('overview.gcHeap')} value={status ? formatBytes(status.gcHeapBytes) : unknown} />
        </Panel>
        <Panel title={t('overview.storage')}>
          <InfoRow label={t('overview.hotDbBytes')} value={overview ? formatBytes(overview.storage.hotDbBytes) : unknown} />
          <InfoRow label={t('overview.hotDbPath')} value={overview?.storage.hotDbPath ?? unknown} />
          <InfoRow label={t('overview.duckDbMemory')} value={overview?.storage.memoryLimit ?? unknown} />
          <InfoRow label={t('overview.cold')} value={overview?.s3Resources ? t(`overview.resourceState.${overview.s3Resources.state}`, { defaultValue: unknown }) : overview ? t(overview.storage.coldEnabled ? 'overview.s3Configured' : 'overview.s3NotConfigured') : unknown} />
          <p className="mt-3 text-xs text-subtle">{t('overview.storageHint')}</p>
        </Panel>
      </div>

      <ResourcePanels resources={status?.resources} local={overview?.localResources} s3={overview?.s3Resources} />

      <Panel title={t('overview.dataStats')}>
        <p className="my-2 text-xs text-subtle">{t('overview.statsHint')}</p>
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-xs">
            <thead><tr className="text-muted">
              {['channel', 'todayRows', 'tables', 'processed', 'dropped', 'health'].map((key, index) =>
                <th key={key} className={cn('px-2 py-2 font-medium', index === 0 ? 'text-left' : 'text-right')}>{t(`overview.${key}`)}</th>)}
            </tr></thead>
            <tbody>
              {Object.entries(overview?.todayRows ?? {}).map(([name, count]) => {
                const channel = overview?.channels.find((item) => item.name === name);
                return <tr key={name} className="border-t border-border/60">
                  <td className="px-2 py-2 font-mono">{name}</td>
                  <td className="px-2 py-2 text-right font-mono">{count}</td>
                  <td className="px-2 py-2 text-right font-mono">{overview?.tableCounts[name] ?? unknown}</td>
                  <td className="px-2 py-2 text-right font-mono">{channel?.processed ?? unknown}</td>
                  <td className={cn('px-2 py-2 text-right font-mono', channel && channel.dropped > 0 && 'text-error')}>{channel?.dropped ?? unknown}</td>
                  <td className={cn('px-2 py-2 text-right', channel?.healthy ? 'text-success' : 'text-warning')}>{channel ? t(channel.healthy ? 'overview.healthy' : 'overview.degraded') : unknown}</td>
                </tr>;
              })}
              {!overview && <tr><td colSpan={6} className="py-4 text-muted">{loading ? t('overview.loading') : unknown}</td></tr>}
            </tbody>
          </table>
        </div>
      </Panel>

      <Panel title={t('settings.mq')}>
        <p className="my-2 text-xs text-subtle">{t('overview.mqHint')}</p>
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-xs">
            <thead><tr className="text-left text-muted">
              {['settings.mqInstanceName', 'settings.mqType', 'overview.enabled', 'overview.runtimeState', 'settings.mqPersisted', 'overview.lastPersisted', 'overview.lastError'].map((key) =>
                <th key={key} className="px-2 py-2 font-medium">{t(key)}</th>)}
            </tr></thead>
            <tbody>
              {consumers.map((item) => <tr key={item.instanceId} className="border-t border-border/60">
                <td className="max-w-48 break-all px-2 py-2">{item.name}</td>
                <td className="px-2 py-2 font-mono">{item.type}</td>
                <td className="px-2 py-2">{t(item.enabled ? 'overview.yes' : 'overview.no')}</td>
                <td className={cn('px-2 py-2', item.state === 'error' ? 'text-error' : 'text-muted')}>{stateLabel(item.state)}</td>
                <td className="px-2 py-2 font-mono">{item.persistedRecords}</td>
                <td className="whitespace-nowrap px-2 py-2 font-mono">{item.lastPersistedAt ? formatDateTime(item.lastPersistedAt) : unknown}</td>
                <td className="px-2 py-2 text-warning">{item.lastError ? t('overview.mqError') : '—'}</td>
              </tr>)}
              {consumers.length === 0 && <tr><td colSpan={7} className="py-4 text-muted">{overview?.mqConsumers ? t('overview.noMq') : loading ? t('overview.loading') : unknown}</td></tr>}
            </tbody>
          </table>
        </div>
      </Panel>
    </div>
  );
}
