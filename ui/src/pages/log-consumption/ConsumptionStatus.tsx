import { useEffect, useState } from 'react';
import { retryMqConsumer } from '@/api/monitor';
import { RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { MqInstance, MqRuntimeItem, MqTopic } from '@/api/types';
import { Button, DataTable, Empty, Tag, Popconfirm, toast } from '@/design-system';
import { formatDateTime } from '@/utils/format';
import { ConsumptionRates, ConsumptionState, consumptionState, hasConsumptionFailure, ratesAvailable, topicRuntime } from './TopicConsumption';

/** Status 只显示实例和配置主题汇总，连接与队列配置留在协议 Tab. */
export function ConsumptionStatus({ instances, runtime, failed, sampledAt, error, writerFailure, notifications = [], onRetry }: {
  instances: MqInstance[];
  runtime: MqRuntimeItem[];
  failed: boolean;
  sampledAt: string | null;
  error?: { code: string; status?: number; at: string } | null;
  writerFailure?: import('@/api/types').MqConsumptionResponse['writerFailure'];
  notifications?: import('@/api/types').MqConsumptionResponse['notifications'];
  onRetry?: () => void;
}) {
  const { t } = useTranslation();
  const [retrying, setRetrying] = useState<number | null>(null);
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const retryInstance = async (id: number) => {
    if (retrying !== null) return;
    setRetrying(id);
    try { await retryMqConsumer(id); toast.success(t('settings.mqRetryQueued')); onRetry?.(); }
    catch { toast.error(t('settings.mqRetryFailed')); }
    finally { setRetrying(null); }
  };
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-3 text-xs text-muted">
        <span>{t('settings.mqStatusHint')}</span>
        {sampledAt && <span>{t('settings.mqSampledAt')}: {formatDateTime(sampledAt)}</span>}
      </div>
      {failed && <div role="alert" className="space-y-2 rounded-md bg-error-soft p-3 text-sm text-error"><div>{t('settings.mqStatusStale')}</div>{error && <div className="font-mono text-xs">{t('settings.mqRequestError.' + error.code)}{error.status ? ` · HTTP ${error.status}` : ''} · {formatDateTime(error.at)}</div>}<Button variant="link" size="sm" onClick={onRetry}>{t('common.retry')}</Button></div>}
      {writerFailure && !writerFailure.recoveredAt && <div role="alert" className="rounded-md border border-error/30 bg-error-soft p-3 text-xs text-error">
        <div>{t('settings.mqStage.persist')} · <span className="font-mono">{writerFailure.code}</span></div>
        <div>{writerFailure.reason}</div>
        <div className="font-mono">{formatDateTime(writerFailure.at)}</div>
      </div>}
      {!instances.length && <Empty description={t('settings.mqNoInstances')} />}
      {instances.map(instance => {
        const item = runtime.find(x => x.instanceId === instance.id);
        const topics = item?.topics ?? [];
        const state = consumptionState(instance.enabled, item?.state);
        const available = Boolean(item) && ratesAvailable(state, failed);
        const sum = (field: 'messagesPerSecond' | 'recordsPerSecond' | 'bytesPerSecond' | 'parseFailures') =>
          topics.reduce((total, topic) => total + topic[field], 0);
        const topicOf = (rule: MqTopic) => topicRuntime(item, rule);
        return (
          <section key={instance.id} className="overflow-hidden rounded-lg border border-border bg-surface">
            <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
              <Tag>{instance.type === 'kafka' ? 'Kafka' : instance.type === 'rabbitmq' ? 'RabbitMQ' : 'Pulsar'}</Tag>
              <span className="text-sm font-medium text-fg">{instance.name}</span>
              <ConsumptionState state={state} warning={instance.enabled && topics.some(topic => topic.failures?.some(failure => failure.stage === 'parse' && !failure.recoveredAt))} error={instance.enabled && topics.some(hasConsumptionFailure)} />
              <ConsumptionRates topic={{ messagesPerSecond: sum('messagesPerSecond'), recordsPerSecond: sum('recordsPerSecond'), bytesPerSecond: sum('bytesPerSecond') }} available={available} />
              {item?.nextRetryAt && <span className="text-xs text-error">{t('settings.mqNextRetry')}: {formatDateTime(item.nextRetryAt)} · #{item.retryAttempt} · {Math.max(0, Math.ceil((Date.parse(item.nextRetryAt) - now) / 1000))}s</span>}
              {instance.enabled && <Popconfirm title={t('settings.mqRetryConfirm')} okText={t('common.retry')} cancelText={t('common.cancel')} onConfirm={() => retryInstance(instance.id)}><Button variant="link" size="sm" disabled={retrying !== null} icon={<RefreshCw className="size-3.5" aria-hidden />}>{t('settings.mqRetryInstance')}</Button></Popconfirm>}
              <span className="text-xs text-muted">{t('settings.mqSessionPersisted')}: <span className="font-mono">{item?.persistedRecords ?? '—'}</span></span>
            </div>
            {item?.lastError && <div className="px-4 py-2 text-xs text-error">{item.lastError}</div>}
            {instance.alertsEnabled && <details className="px-4 py-2 text-xs text-muted"><summary className="cursor-pointer">{t('settings.mqNotificationState')}</summary>{notifications.filter(value => value.instanceId === instance.id).map(value => <div key={value.topic + value.channelId} className="mt-1 break-all font-mono">{value.topic || instance.name} · {t('settings.mqDelivery.' + value.status)} · {t('settings.mqSuppressed', { count: value.suppressedCount })} · {formatDateTime(value.suppressedUntil)}</div>)}</details>}
            <DataTable<MqTopic>
              dataSource={instance.topics}
              rowKey={rule => (rule.matchMode ?? 'exact') + ':' + rule.name}
              pagination={false}
              columns={[
                { key: 'topic', title: t('settings.mqName'), render: (_, rule) => <><span className="break-all font-mono">{rule.name}{rule.matchMode === 'prefix' ? '*' : ''}</span>{rule.matchMode === 'prefix' && <span className="ml-2 text-xs text-muted">{t('settings.mqMatchedCount', { count: topicOf(rule)?.matchedTopics ?? 0 })}</span>}</> },
                { key: 'state', title: t('settings.mqStatus'), render: (_, rule) => <><ConsumptionState state={consumptionState(instance.enabled, topicOf(rule)?.state)} warning={instance.enabled && Boolean(topicOf(rule)?.failures?.some(failure => failure.stage === 'parse' && !failure.recoveredAt))} error={instance.enabled && hasConsumptionFailure(topicOf(rule))} />{instance.enabled && topicOf(rule)?.lastError && <div className="mt-1 text-xs text-error">{topicOf(rule)?.lastError}</div>}</> },
                { key: 'diagnostics', title: t('settings.mqDiagnostics'), render: (_, rule) => {
                  const topic = topicOf(rule);
                  const waiting = topic?.stageSince ? Math.max(0, Math.floor(((sampledAt ? Date.parse(sampledAt) : Date.now()) - Date.parse(topic.stageSince)) / 1000)) : 0;
                  return <div className="min-w-48 space-y-1 text-xs"><div>{t('settings.mqStage.' + (topic?.stage ?? 'unknown'))}{waiting > 0 && <span className="ml-2 font-mono">{waiting}s</span>}</div>{waiting >= 30 && <div className="text-warning">{t('settings.mqWaitingLong')}</div>}<div className="text-muted">{t('settings.mqPendingPersistence')}: {topic?.pendingPersistence ?? '—'} · {t('settings.mqPendingConfirmation')}: {topic?.pendingConfirmation ?? '—'}</div>{(topic?.failures?.length ?? 0) > 0 && <details><summary className="cursor-pointer text-error">{t('settings.mqRecentFailures')}</summary>{topic?.failures?.map(failure => <div key={failure.stage} className="mt-2 space-y-1 border-t border-border pt-2"><div>{t('settings.mqStage.' + failure.stage)} · <span className="font-mono">{failure.code}</span></div><div className="break-words">{failure.reason}</div><div className="font-mono text-muted">{formatDateTime(failure.at)} · ×{failure.occurrences}</div>{failure.recoveredAt && <div className="text-muted">{t('settings.mqRecovered')}: {formatDateTime(failure.recoveredAt)}</div>}</div>)}</details>}</div>;
                } },
                { key: 'speed', title: t('settings.mqSpeed'), mono: true, render: (_, rule) => {
                  const topic = topicOf(rule);
                  return <ConsumptionRates topic={topic} available={ratesAvailable(consumptionState(instance.enabled, topic?.state), failed)} />;
                } },
                { key: 'persisted', title: t('settings.mqSessionPersisted'), mono: true, render: (_, rule) => topicOf(rule)?.persistedRecords ?? '—' },
                { key: 'parse', title: t('settings.mqParseFailures'), mono: true, render: (_, rule) => <>{topicOf(rule)?.parseFailures ?? '—'}<div className="text-xs text-muted">{t('settings.mqBlocked')}: {topicOf(rule)?.blockedMessages ?? '—'}</div></> },
                { key: 'confirmed', title: t('settings.mqConfirmed'), mono: true, render: (_, rule) => <>{topicOf(rule)?.confirmedMessages ?? '—'}<div className="text-xs text-muted">{t('settings.mqConfirmFailed')}: {topicOf(rule)?.confirmationFailures ?? '—'}</div></> },
                { key: 'last', title: t('settings.mqLastPersisted'), mono: true, render: (_, rule) => <span className="whitespace-nowrap">{formatDateTime(topicOf(rule)?.lastPersistedAt)}</span> },
              ]}
            />
          </section>
        );
      })}
    </div>
  );
}
