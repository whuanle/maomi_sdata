import { useTranslation } from 'react-i18next';
import type { MqRuntimeItem, MqTopic, MqTopicRuntimeItem } from '@/api/types';
import { Tag } from '@/design-system';
import { formatBytesRate, formatRate } from '@/utils/format';

export function topicRuntime(item: MqRuntimeItem | undefined, rule: MqTopic) {
  return item?.topics?.find(topic => topic.name === rule.name && topic.matchMode === (rule.matchMode ?? 'exact'));
}

export function consumptionState(enabled: boolean, state?: string) {
  return enabled ? state ?? 'unknown' : 'disabled';
}

export function ratesAvailable(state: string, failed: boolean) {
  return !failed && ['connected', 'consuming', 'persisting', 'idle', 'error'].includes(state);
}

export function hasConsumptionFailure(topic?: MqTopicRuntimeItem) {
  return Boolean(topic && (topic.state === 'error' || topic.lastError || topic.failures?.some(failure => !failure.recoveredAt)));
}

export function ConsumptionState({ state, warning = false, error = false }: { state: string; warning?: boolean; error?: boolean }) {
  const { t } = useTranslation();
  return <><Tag tone={state === 'error' || warning || error ? 'error' : state === 'consuming' ? 'success' : undefined}>{t('settings.mqState.' + ((warning || error) && state === 'consuming' ? 'error' : state))}</Tag>{warning && <Tag tone="error">{t('settings.mqParseWarning')}</Tag>}{error && !warning && state !== 'error' && <Tag tone="error">{t('settings.mqRecentFailures')}</Tag>}</>;
}

export function ConsumptionRates({ topic, available }: {
  topic?: Pick<MqTopicRuntimeItem, 'messagesPerSecond' | 'recordsPerSecond' | 'bytesPerSecond'>;
  available: boolean;
}) {
  const { t } = useTranslation();
  const show = available && topic;
  return <span className="font-mono text-xs text-muted">{show ? formatRate(topic.messagesPerSecond) : '—'} {t('settings.mqMessagesUnit')} · {show ? formatRate(topic.recordsPerSecond) : '—'} {t('settings.mqRecordsUnit')} · {show ? formatBytesRate(topic.bytesPerSecond) : '—'}</span>;
}

export function TopicConsumption({ topic, enabled, failed }: {
  topic?: MqTopicRuntimeItem;
  enabled: boolean;
  failed: boolean;
}) {
  const state = consumptionState(enabled, topic?.state);
  return (
    <div className="flex min-w-0 flex-wrap items-center gap-2">
      <ConsumptionState state={state} warning={enabled && Boolean(topic?.failures?.some(failure => failure.stage === 'parse' && !failure.recoveredAt))} error={enabled && hasConsumptionFailure(topic)} />
      <ConsumptionRates topic={topic} available={ratesAvailable(state, failed)} />
      {enabled && topic?.lastError && <div className="w-full break-words text-xs text-error">{topic.lastError}</div>}
    </div>
  );
}
