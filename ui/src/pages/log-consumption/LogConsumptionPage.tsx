import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { FlaskConical, Pencil, Plus, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { fetchNotificationChannels, fetchMqInstances, parseMqPreview, saveMqInstances } from '@/api/settings';
import { fetchMqConsumption, MqStatusError } from '@/api/monitor';
import { ConsumptionStatus } from './ConsumptionStatus';
import { TopicConsumption, topicRuntime } from './TopicConsumption';
import { formatDateTime } from '@/utils/format';
import type { MqRuntimeItem } from '@/api/types';
import type { MqInstance, MqParsePreviewResult, MqParserOptions, MqTopic } from '@/api/types';
import {
  Button,
  Checkbox,
  Drawer,
  Empty,
  Field,
  Input,
  Page,
  Popconfirm,
  QueryBar,
  Select,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Tag,
  Textarea,
  toast,
} from '@/design-system';
import { isAdminRole, useAuthStore } from '@/store/auth';

/** 实例类型与解析规则白名单（与后端一致）. */
const MQ_TYPES = ['kafka', 'rabbitmq', 'pulsar'];
const MQ_FORMATS = ['otlp-proto', 'otlp-json', 'json', 'regex', 'csv', 'logfmt', 'syslog', 'raw'];
const FORMATS_WITH_PARSER = new Set(['json', 'regex', 'csv', 'logfmt', 'syslog']);

/** 解析规则参数提示文案的 i18n key（按 format）. */
const PARSER_HINT_KEYS: Record<string, string> = {
  json: 'settings.mqParserHintJson',
  regex: 'settings.mqParserHintRegex',
  csv: 'settings.mqParserHintCsv',
  logfmt: 'settings.mqParserHintLogfmt',
  syslog: 'settings.mqParserHintSyslog',
};

/** 表单错误消息（ApiError.message 为后端人类可读中文）. */
function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** 空解析规则参数. */
function emptyParser(): MqParserOptions {
  return {
    timestampField: '',
    timestampFormat: '',
    severityField: '',
    serviceField: '',
    bodyField: '',
    traceIdField: '',
    spanIdField: '',
    pattern: '',
    delimiter: ',',
    keepUnmapped: true,
  };
}

/** 新 topic 默认值（raw：整条消息为正文，最常见起点）. */
function newTopic(): MqTopic {
  return { name: '', format: 'raw', service: '', severity: 'INFO', parser: null };
}

/** 新实例默认值（id=0 由后端保存时补雪花 id）. */
function newInstance(type: string): MqInstance {
  return {
    id: 0,
    name: '',
    type,
    enabled: true,
    bootstrapServers: '',
    groupId: 'sdata-logs',
    autoOffsetReset: 'earliest',
    uri: '',
    serviceUrl: '',
    subscriptionName: 'sdata-logs',
    subscriptionType: 'Shared',
    topics: [],
  };
}

/** 实例编辑抽屉打开时的草稿深拷贝（编辑态不直接改原实例）. */
function draftOf(instance: MqInstance | null): MqInstance {
  const source = instance ?? newInstance('kafka');
  return {
    ...newInstance(source.type),
    ...source,
    topics: (source.topics ?? []).map((topic) => ({ ...topic, parser: topic.parser ? { ...topic.parser } : null })),
  };
}

/** 连接配置校验错误（语义与原 antd Form rules 一致：required 提示即字段名）. */
interface DrawerErrors {
  name?: string;
  bootstrapServers?: string;
  uri?: string;
  serviceUrl?: string;
}

/** 单个 topic 的校验错误（name 必填；regex 的 pattern 必填）. */
interface TopicErrors {
  name?: string;
  pattern?: string;
}

function ConfigSection({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <section className="rounded-lg border border-border bg-surface">
      <div className="border-b border-border px-4 py-3">
        <h3 className="text-sm font-semibold text-fg">{title}</h3>
        {description && <p className="mt-1 text-xs leading-relaxed text-muted">{description}</p>}
      </div>
      <div className="grid grid-cols-1 gap-4 p-4 sm:grid-cols-2">{children}</div>
    </section>
  );
}

function ConfigValue({ label, value }: { label: string; value: string }) {
  return (
    <span className="inline-flex min-w-0 max-w-full items-baseline gap-1.5 text-xs leading-6">
      <span className="shrink-0 text-muted">{label}</span>
      <span className="min-w-0 truncate font-mono text-fg" title={value}>{value}</span>
    </span>
  );
}

/** 仅展示当前解析格式生效的队列参数，不读取实例凭据. */
function TopicSummary({ topic }: { topic: MqTopic }) {
  const { t } = useTranslation();
  const parser = topic.parser;
  const mappings: [keyof MqParserOptions, string][] = [
    ['timestampField', 'mqTimestampField'],
    ['timestampFormat', 'mqTimeFormatLabel'],
    ['severityField', 'mqSeverityField'],
    ['serviceField', 'mqServiceField'],
    ['bodyField', 'mqBodyFieldLabel'],
    ['traceIdField', 'mqTraceIdField'],
    ['spanIdField', 'mqSpanIdField'],
  ];
  const configured = mappings.filter(([key]) => parser?.[key]);
  return (
    <div className="grid min-w-0 grid-cols-1 gap-x-5 gap-y-2 md:grid-cols-[minmax(160px,1fr)_minmax(0,4fr)]">
      <span className="min-w-0 break-all font-mono text-[13px] font-medium leading-6 text-fg">{topic.name}</span>
      <div className="flex min-w-0 flex-wrap items-baseline gap-x-5 gap-y-1">
        <ConfigValue label={t('settings.mqMatchMode')} value={t(topic.matchMode === 'prefix' ? 'settings.mqPrefix' : 'settings.mqExact')} />
        <ConfigValue label={t('settings.mqFormat')} value={topic.format || 'raw'} />
        <ConfigValue label={t('settings.mqService')} value={topic.service || t('settings.mqDefaultService')} />
        <ConfigValue label={t('settings.mqSeverity')} value={topic.severity || 'INFO'} />
        {FORMATS_WITH_PARSER.has(topic.format) && (
          <>
            {topic.format === 'regex' && parser?.pattern && <ConfigValue label={t('settings.mqPatternLabel')} value={parser.pattern} />}
            {topic.format === 'csv' && <ConfigValue label={t('settings.mqDelimiterLabel')} value={JSON.stringify(parser?.delimiter || ',')} />}
            {configured.map(([key, label]) => <ConfigValue key={key} label={t('settings.' + label)} value={String(parser?.[key])} />)}
            {configured.length === 0 && <span className="text-xs leading-6 text-muted">{t('settings.mqDefaultMapping')}</span>}
            <ConfigValue label={t('settings.mqUnmappedLabel')} value={t(parser?.keepUnmapped === false ? 'settings.mqUnmappedDiscard' : 'settings.mqUnmappedKeep')} />
          </>
        )}
      </div>
    </div>
  );
}

/** 解析规则参数子表单（按 format 显示 pattern/delimiter/字段映射）. */
function TopicParserFields({ topic, patternError, onParserChange }: {
  topic: MqTopic;
  patternError?: string;
  onParserChange: (patch: Partial<MqParserOptions>) => void;
}) {
  const { t } = useTranslation();
  const format = topic.format;
  const hintKey = PARSER_HINT_KEYS[format];
  if (!format || !FORMATS_WITH_PARSER.has(format)) {
    return <p className="mb-2 text-xs text-muted">{t('settings.mqParserHintNone')}</p>;
  }

  const parser = topic.parser;
  return (
    <div className="space-y-4">
      {hintKey && <p className="text-xs leading-relaxed text-muted">{t(hintKey)}</p>}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        {format === 'regex' && (
          <Field label={t('settings.mqPattern')} required error={patternError} className="sm:col-span-2">
            <Input
              value={parser?.pattern ?? ''}
              onChange={(event) => onParserChange({ pattern: event.target.value })}
              invalid={Boolean(patternError)}
            />
          </Field>
        )}
        {format === 'csv' && (
          <Field label={t('settings.mqDelimiter')}>
            <Input maxLength={1} value={parser?.delimiter ?? ''} onChange={(event) => onParserChange({ delimiter: event.target.value })} />
          </Field>
        )}
        <Field label={t('settings.mqTimestampField')}>
          <Input value={parser?.timestampField ?? ''} onChange={(event) => onParserChange({ timestampField: event.target.value })} />
        </Field>
        <Field label={t('settings.mqTimestampFormat')}>
          <Input value={parser?.timestampFormat ?? ''} onChange={(event) => onParserChange({ timestampFormat: event.target.value })} />
        </Field>
        <Field label={t('settings.mqSeverityField')}>
          <Input value={parser?.severityField ?? ''} onChange={(event) => onParserChange({ severityField: event.target.value })} />
        </Field>
        <Field label={t('settings.mqServiceField')}>
          <Input value={parser?.serviceField ?? ''} onChange={(event) => onParserChange({ serviceField: event.target.value })} />
        </Field>
        <Field label={t('settings.mqBodyField')}>
          <Input value={parser?.bodyField ?? ''} onChange={(event) => onParserChange({ bodyField: event.target.value })} />
        </Field>
        <Field label={t('settings.mqTraceIdField')}>
          <Input value={parser?.traceIdField ?? ''} onChange={(event) => onParserChange({ traceIdField: event.target.value })} />
        </Field>
        <Field label={t('settings.mqSpanIdField')}>
          <Input value={parser?.spanIdField ?? ''} onChange={(event) => onParserChange({ spanIdField: event.target.value })} />
        </Field>
        <Field label={t('settings.mqKeepUnmapped')}>
          <div className="flex h-8 items-center">
            <Switch
              checked={parser?.keepUnmapped ?? true}
              onChange={(checked) => onParserChange({ keepUnmapped: checked })}
              aria-label={t('settings.mqKeepUnmapped')}
            />
          </div>
        </Field>
      </div>
    </div>
  );
}

/** 实例编辑抽屉：连接配置（按类型）+ Topics 解析规则 + 测试解析预览. */
function InstanceDrawer({ open, instance, saving, onClose, onSave }: {
  open: boolean;
  instance: MqInstance | null;
  saving: boolean;
  onClose: () => void;
  onSave: (instance: MqInstance) => void;
}) {
  const { t } = useTranslation();
  const { watch, setValue, reset } = useForm<{ instance: MqInstance }>({ defaultValues: { instance: draftOf(instance) } });
  const draft = watch('instance');
  const setDraft = (value: MqInstance | ((previous: MqInstance) => MqInstance)) => {
    setValue('instance', typeof value === 'function' ? value(draft) : value, { shouldDirty: true });
  };
  const [channels, setChannels] = useState<import('@/api/types').NotificationChannel[]>([]);
  const [channelError, setChannelError] = useState(false);
  useEffect(() => {
    if (!open) return;
    let active = true;
    setChannelError(false);
    void fetchNotificationChannels().then(value => { if (active) setChannels(value); }).catch(() => { if (active) setChannelError(true); });
    return () => { active = false; };
  }, [open]);
  const [errors, setErrors] = useState<DrawerErrors>({});
  const [topicErrors, setTopicErrors] = useState<Record<number, TopicErrors>>({});
  const [samples, setSamples] = useState<Record<number, string>>({});
  const [previews, setPreviews] = useState<Record<number, MqParsePreviewResult>>({});
  const [previewing, setPreviewing] = useState<Record<number, boolean>>({});

  useEffect(() => {
    if (open) {
      reset({ instance: draftOf(instance) });
      setErrors({});
      setTopicErrors({});
      setSamples({});
      setPreviews({});
      setPreviewing({});
    }
  }, [open, instance]);

  /** 更新连接配置并清除对应字段的校验错误. */
  const update = (patch: Partial<MqInstance>) => {
    setDraft((prev) => ({ ...prev, ...patch }));
    const keys = Object.keys(patch) as (keyof DrawerErrors)[];
    setErrors((prev) => {
      if (!keys.some((key) => prev[key] !== undefined)) {
        return prev;
      }

      const next = { ...prev };
      keys.forEach((key) => {
        delete next[key];
      });
      return next;
    });
  };

  /** 更新单个 topic 基础字段并清除对应校验错误. */
  const updateTopic = (index: number, patch: Partial<MqTopic>) => {
    setDraft((prev) => ({
      ...prev,
      topics: prev.topics.map((topic, i) => (i === index ? { ...topic, ...patch } : topic)),
    }));
    if ('name' in patch) {
      setTopicErrors((prev) => (prev[index]?.name ? { ...prev, [index]: { ...prev[index], name: undefined } } : prev));
    }
  };

  /** 更新单个 topic 的解析规则参数（首改时以 emptyParser 补齐缺省）. */
  const updateParser = (index: number, patch: Partial<MqParserOptions>) => {
    setDraft((prev) => ({
      ...prev,
      topics: prev.topics.map((topic, i) =>
        i === index ? { ...topic, parser: { ...emptyParser(), ...topic.parser, ...patch } } : topic,
      ),
    }));
    if ('pattern' in patch) {
      setTopicErrors((prev) => (prev[index]?.pattern ? { ...prev, [index]: { ...prev[index], pattern: undefined } } : prev));
    }
  };

  const addTopic = () => {
    setDraft((prev) => ({ ...prev, topics: [...prev.topics, newTopic()] }));
  };

  const removeTopic = (index: number) => {
    setDraft((prev) => ({ ...prev, topics: prev.topics.filter((_, i) => i !== index) }));
    setSamples({});
    setPreviews({});
    setPreviewing({});
    setTopicErrors((prev) => {
      const next: Record<number, TopicErrors> = {};
      Object.entries(prev).forEach(([key, value]) => {
        const oldIndex = Number(key);
        if (oldIndex !== index) {
          next[oldIndex > index ? oldIndex - 1 : oldIndex] = value;
        }
      });
      return next;
    });
  };

  const runPreview = async (index: number) => {
    const topic = draft.topics[index];
    if (!topic) {
      return;
    }

    const sample = samples[index] ?? '';
    if (!sample.trim()) {
      toast.warning(t('settings.mqSample'));
      return;
    }

    setPreviewing((prev) => ({ ...prev, [index]: true }));
    try {
      const result = await parseMqPreview({
        format: topic.format,
        service: topic.service ?? '',
        severity: topic.severity ?? 'INFO',
        parser: FORMATS_WITH_PARSER.has(topic.format) ? (topic.parser ?? emptyParser()) : null,
        sample,
      });
      setPreviews((prev) => ({ ...prev, [index]: result }));
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setPreviewing((prev) => ({ ...prev, [index]: false }));
    }
  };

  /** 提交：全量校验（required 提示文案与原 antd Form rules 一致），失败展示字段错误不保存. */
  const submit = () => {
    const fieldErrors: DrawerErrors = {};
    if (!draft.name) {
      fieldErrors.name = t('settings.mqInstanceName');
    }
    if (draft.type === 'kafka' && !draft.bootstrapServers) {
      fieldErrors.bootstrapServers = t('settings.mqServers');
    }
    if (draft.type === 'rabbitmq' && !draft.uri) {
      fieldErrors.uri = 'AMQP URI';
    }
    if (draft.type === 'pulsar' && !draft.serviceUrl) {
      fieldErrors.serviceUrl = 'Service URL';
    }

    const nextTopicErrors: Record<number, TopicErrors> = {};
    draft.topics.forEach((topic, index) => {
      const item: TopicErrors = {};
      if (!topic.name) {
        item.name = t('settings.mqName');
      }
      if (topic.format === 'regex' && !topic.parser?.pattern) {
        item.pattern = t('settings.mqPattern');
      }
      if (item.name || item.pattern) {
        nextTopicErrors[index] = item;
      }
    });

    setErrors(fieldErrors);
    setTopicErrors(nextTopicErrors);
    if (Object.keys(fieldErrors).length > 0 || Object.keys(nextTopicErrors).length > 0) {
      return;
    }

    const topics = draft.topics
      .filter((x) => x.name.trim())
      .map((x) => ({ ...x, parser: FORMATS_WITH_PARSER.has(x.format) ? (x.parser ?? emptyParser()) : null }));
    // 未在表单展示的字段（其他类型的连接参数/默认值）用默认值补齐；编辑时保留实例 id
    onSave({ ...newInstance(draft.type), ...draft, id: instance?.id ?? 0, topics } as MqInstance);
  };

  return (
    <Drawer
      title={instance?.id ? t('settings.mqEdit') : t('settings.mqNew')}
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      width={860}
      className="bg-bg"
      footer={<><Button onClick={onClose}>{t('common.cancel')}</Button><Button variant="primary" loading={saving} onClick={submit}>{t('common.save')}</Button></>}
    >
      <div className="flex flex-col gap-5">
        <ConfigSection title={t('settings.mqBasicSection')}>
          <Field label={t('settings.mqInstanceName')} required error={errors.name} className="min-w-0 sm:col-span-2">
            <Input
              maxLength={128}
              placeholder={t('settings.mqInstanceName')}
              value={draft.name}
              onChange={(event) => update({ name: event.target.value })}
              invalid={Boolean(errors.name)}
            />
          </Field>
          <Field label={t('settings.mqType')} className="min-w-0">
            <Select
              className="w-full"
              value={draft.type}
              disabled={Boolean(instance?.id)}
              onValueChange={(value) => update({ type: value })}
              options={MQ_TYPES.map((x) => ({ value: x, label: x }))}
            />
          </Field>
          <Field label={t('settings.mqEnabled')} className="min-w-0">
            <div className="flex h-8 items-center">
              <Switch checked={draft.enabled} onChange={(checked) => update({ enabled: checked })} aria-label={t('settings.mqEnabled')} />
            </div>
          </Field>
        </ConfigSection>

        <ConfigSection title={t('settings.mqRetryAlerts')}>
          <Field label={t('settings.mqRetryInitial')}><Input type="number" min={1} max={300} value={draft.retryInitialSeconds ?? 5} onChange={event => update({ retryInitialSeconds: Number(event.target.value) })} /></Field>
          <Field label={t('settings.mqRetryMaximum')}><Input type="number" min={1} max={3600} value={draft.retryMaxSeconds ?? 60} onChange={event => update({ retryMaxSeconds: Number(event.target.value) })} /></Field>
          <Field label={t('settings.mqAlertsEnabled')}><Switch checked={draft.alertsEnabled ?? false} onChange={checked => update({ alertsEnabled: checked })} aria-label={t('settings.mqAlertsEnabled')} /></Field>
          <Field label={t('settings.mqAlertCooldown')}><Input type="number" min={1} max={1440} value={draft.alertCooldownMinutes ?? 10} onChange={event => update({ alertCooldownMinutes: Number(event.target.value) })} /></Field>
          <div className="space-y-2 sm:col-span-2">
            <div className="text-xs text-muted">{t('settings.mqChannelsHint')}</div>
            {channelError && <div role="alert" className="text-xs text-error">{t('settings.mqChannelsFailed')}</div>}
            {channels.map(channel => <div key={channel.id}><Checkbox disabled={!channel.enabled} checked={(draft.notificationChannelIds ?? []).includes(channel.id)} label={channel.name} onChange={event => update({ notificationChannelIds: event.target.checked ? [...(draft.notificationChannelIds ?? []), channel.id] : (draft.notificationChannelIds ?? []).filter(id => id !== channel.id) })} /></div>)}
          </div>
        </ConfigSection>

        <ConfigSection title={t('settings.mqConnectionSection')}>
          {draft.type === 'kafka' && (
            <>
              <Field label={t('settings.mqServers')} required error={errors.bootstrapServers} className="min-w-0 sm:col-span-2">
                <Input
                  placeholder="broker1:9092,broker2:9092"
                  value={draft.bootstrapServers}
                  onChange={(event) => update({ bootstrapServers: event.target.value })}
                  invalid={Boolean(errors.bootstrapServers)}
                />
              </Field>
              <Field label={t('settings.mqGroupId')} className="min-w-0">
                <Input value={draft.groupId} onChange={(event) => update({ groupId: event.target.value })} />
              </Field>
              <Field label={t('settings.mqOffset')} className="min-w-0">
                <Select
                  className="w-full"
                  value={draft.autoOffsetReset}
                  onValueChange={(value) => update({ autoOffsetReset: value })}
                  options={[{ value: 'earliest', label: 'earliest' }, { value: 'latest', label: 'latest' }]}
                />
              </Field>
            </>
          )}
          {draft.type === 'rabbitmq' && (
            <Field label={t('settings.mqAmqpUri')} required error={errors.uri} className="sm:col-span-2">
              <Input placeholder="amqp://guest:guest@localhost:5672/" value={draft.uri} onChange={(event) => update({ uri: event.target.value })} invalid={Boolean(errors.uri)} />
            </Field>
          )}
          {draft.type === 'pulsar' && (
            <>
              <Field label={t('settings.mqServiceUrl')} required error={errors.serviceUrl} className="min-w-0 sm:col-span-2">
                <Input placeholder="pulsar://localhost:6650" value={draft.serviceUrl} onChange={(event) => update({ serviceUrl: event.target.value })} invalid={Boolean(errors.serviceUrl)} />
              </Field>
              <Field label={t('settings.mqSubscription')}>
                <Input value={draft.subscriptionName} onChange={(event) => update({ subscriptionName: event.target.value })} />
              </Field>
              <Field label={t('settings.mqSubscriptionType')}>
                <Select value={draft.subscriptionType} onValueChange={(value) => update({ subscriptionType: value })} options={['Shared', 'Failover', 'Exclusive', 'KeyShared'].map((value) => ({ value, label: value }))} />
              </Field>
            </>
          )}
        </ConfigSection>
        <ConfigSection title={t(draft.type === 'kafka' ? 'settings.mqSecuritySection' : 'settings.mqDiscoverySection')}>
          {draft.type === 'kafka' && (
            <>
              <Field label={t('settings.mqSecurityProtocol')}>
                <Select value={draft.securityProtocol ?? 'Plaintext'} onValueChange={(value) => update({ securityProtocol: value })}
                  options={['Plaintext', 'SaslPlaintext', 'SaslSsl', 'Ssl'].map((value) => ({ value, label: value }))} />
              </Field>
              <Field label={t('settings.mqSaslMechanism')}>
                <Select value={draft.saslMechanism ?? 'ScramSha256'} onValueChange={(value) => update({ saslMechanism: value })}
                  options={['Plain', 'ScramSha256', 'ScramSha512'].map((value) => ({ value, label: value }))} />
              </Field>
              <Field label={t('settings.mqAdminUsername')}>
                <Input value={draft.saslUsername ?? ''} onChange={(event) => update({ saslUsername: event.target.value })} />
              </Field>
              <Field label={t('settings.mqAdminPassword')}>
                <Input type="password" value={draft.saslPassword ?? ''} onChange={(event) => update({ saslPassword: event.target.value })} />
              </Field>
            </>
          )}
          {draft.type !== 'kafka' && (
            <>
              <Field label={t('settings.mqAdminUrl')}>
                <Input value={draft.adminUrl ?? ''} onChange={(event) => update({ adminUrl: event.target.value })} />
              </Field>
              {draft.type === 'rabbitmq' && (
                <Field label={t('settings.mqAdminUsername')}>
                  <Input value={draft.adminUsername ?? ''} onChange={(event) => update({ adminUsername: event.target.value })} />
                </Field>
              )}
              <Field label={t('settings.mqAdminPassword')}>
                <Input type="password" value={draft.adminPassword ?? ''} onChange={(event) => update({ adminPassword: event.target.value })} />
              </Field>
              {draft.type === 'pulsar' && (
                <Field label={t('settings.mqNamespace')}>
                  <Input value={draft.namespace ?? 'public/default'} onChange={(event) => update({ namespace: event.target.value })} />
                </Field>
              )}
            </>
          )}
        </ConfigSection>
        <section className="space-y-3">
          <div>
            <h3 className="text-sm font-semibold text-fg">{t('settings.mqTopics')}</h3>
            <p className="mt-1 text-xs leading-relaxed text-muted">{t('settings.mqMatchHint')}</p>
          </div>
          {draft.topics.map((topic, index) => (
            <div key={index} className="overflow-hidden rounded-lg border border-border bg-surface">
              <div className="flex items-center justify-between gap-3 border-b border-border bg-surface-2 px-4 py-2">
                <span className="text-xs font-medium text-muted">{t('settings.mqRuleNumber', { number: index + 1 })}</span>
                <Popconfirm title={t('common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => removeTopic(index)}>
                  <Button variant="link" size="sm" danger icon={<Trash2 className="size-3.5" aria-hidden />}>{t('common.delete')}</Button>
                </Popconfirm>
              </div>
              <div className="grid grid-cols-1 gap-4 p-4 sm:grid-cols-2">
                <Field label={t('settings.mqName')} required error={topicErrors[index]?.name} className="min-w-0 sm:col-span-2">
                  <Input
                    placeholder={t('settings.mqName')}
                    value={topic.name}
                    onChange={(event) => updateTopic(index, { name: event.target.value })}
                    invalid={Boolean(topicErrors[index]?.name)}
                  />
                </Field>
                <Field label={t('settings.mqMatchMode')} className="min-w-0">
                  <Select value={topic.matchMode ?? 'exact'} onValueChange={(value) => updateTopic(index, { matchMode: value as 'exact' | 'prefix' })}
                    options={[{ value: 'exact', label: t('settings.mqExact') }, { value: 'prefix', label: t('settings.mqPrefix') }]} />
                </Field>
                <Field label={t('settings.mqFormat')} className="min-w-0">
                  <Select
                    className="w-full"
                    value={topic.format}
                    onValueChange={(value) => updateTopic(index, { format: value })}
                    options={MQ_FORMATS.map((x) => ({ value: x, label: x }))}
                  />
                </Field>
                <Field label={t('settings.mqService')} className="min-w-0">
                  <Input
                    placeholder={t('settings.mqService')}
                    value={topic.service}
                    onChange={(event) => updateTopic(index, { service: event.target.value })}
                  />
                </Field>
                <Field label={t('settings.mqSeverity')} className="min-w-0">
                  <Select
                    className="w-full"
                    value={topic.severity}
                    onValueChange={(value) => updateTopic(index, { severity: value })}
                    options={['INFO', 'WARN', 'ERROR', 'DEBUG', 'TRACE'].map((x) => ({ value: x, label: x }))}
                  />
                </Field>
              </div>
              <details open={topic.format === 'regex' || topicErrors[index]?.pattern ? true : undefined} className="border-t border-border px-4 py-3">
                <summary className="cursor-pointer text-xs font-medium text-fg">{t('settings.mqParserRules')}</summary>
                <div className="pt-4"><TopicParserFields topic={topic} patternError={topicErrors[index]?.pattern} onParserChange={(patch) => updateParser(index, patch)} /></div>
              </details>
              <details className="border-t border-border px-4 py-3">
                <summary className="cursor-pointer text-xs font-medium text-fg">{t('settings.mqTestParse')}</summary>
                <div className="flex flex-col items-start gap-3 pt-4">
                  <Textarea
                    rows={2}
                    className="w-full font-mono"
                    placeholder={t('settings.mqSample')}
                    value={samples[index] ?? ''}
                    onChange={(event) => setSamples((prev) => ({ ...prev, [index]: event.target.value }))}
                  />
                  <div className="w-full min-w-0">
                    <Button
                      icon={<FlaskConical className="size-3.5" aria-hidden />}
                      loading={Boolean(previewing[index])}
                      onClick={() => void runPreview(index)}
                    >
                      {t('settings.mqTestParse')}
                    </Button>
                    {previews[index] && <PreviewResult result={previews[index]} />}
                  </div>
                </div>
              </details>
            </div>
          ))}
          <Button block className="border-dashed" icon={<Plus className="size-3.5" aria-hidden />} onClick={addTopic}>
            {t('settings.mqAdd')}
          </Button>
        </section>
      </div>
    </Drawer>
  );
}

/** 解析预览结果（记录列表 + 失败提示）. */
function PreviewResult({ result }: { result: MqParsePreviewResult }) {
  const { t } = useTranslation();
  return (
    <div className="mt-2">
      {!result.success && (
        <div className="mb-1 rounded-md border border-warning/30 bg-warning-soft px-2 py-1 text-xs text-warning">
          {`${t('settings.mqPreviewParseFailed')}${result.error ?? ''}`}
        </div>
      )}
      {result.records.map((record, index) => (
        <div
          key={index}
          className={`mt-0.5 font-mono text-xs break-all ${record.attributesJson?.includes('_parse_error') ? 'text-error' : 'text-fg'}`}
        >
          {`[${record.severity}] ${record.service} · ${record.body}`}
        </div>
      ))}
    </div>
  );
}

/** 日志消费页：MQ 多实例接入（Kafka/RabbitMQ/Pulsar），保存后自动热更新；设置仅管理员，非管理员展示提示. */
export function LogConsumptionPage() {
  const { t } = useTranslation();
  const role = useAuthStore((state) => state.role);
  const viewerIsAdmin = isAdminRole(role);
  const [instances, setInstances] = useState<MqInstance[] | null>(null);
  const [saving, setSaving] = useState(false);
  const [runtime, setRuntime] = useState<MqRuntimeItem[]>([]);
  const [statusFailed, setStatusFailed] = useState(false);
  const [notifications, setNotifications] = useState<import('@/api/types').MqConsumptionResponse['notifications']>([]);
  const [writerFailure, setWriterFailure] = useState<import('@/api/types').MqConsumptionResponse['writerFailure']>(null);
  const [statusError, setStatusError] = useState<{ code: string; status?: number; at: string } | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [sampledAt, setSampledAt] = useState<string | null>(null);
  const [typeFilter, setTypeFilter] = useState<string>('status');
  const [editor, setEditor] = useState<{ open: boolean; instance: MqInstance | null }>({ open: false, instance: null });

  const load = useCallback(async () => {
    try {
      setInstances(await fetchMqInstances());
    } catch (error) {
      toast.error(errorMessage(error));
      setInstances([]);
    }
  }, []);

  useEffect(() => {
    if (viewerIsAdmin) {
      void load();
    }
  }, [viewerIsAdmin, load]);

  const persist = async (next: MqInstance[]) => {
    setSaving(true);
    try {
      await saveMqInstances(next);
      toast.success(t('common.save'));
      await load();
      setEditor({ open: false, instance: null });
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  useEffect(() => {
    if (!viewerIsAdmin) return;
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const refresh = async () => {
      try {
        const status = await fetchMqConsumption();
        if (active) {
          setRuntime(status.instances);
          setNotifications(status.notifications ?? []);
          setWriterFailure(status.writerFailure);
          setSampledAt(status.sampledAt);
          setStatusFailed(false);
          setStatusError(null);
        }
      } catch (error) {
        if (active) {
          setStatusFailed(true);
          setStatusError({ code: error instanceof MqStatusError ? error.code : 'request', status: error instanceof MqStatusError ? error.status : undefined, at: new Date().toISOString() });
        }
      } finally {
        if (active) timer = setTimeout(() => void refresh(), 10000);
      }
    };
    void refresh();
    return () => { active = false; clearTimeout(timer); };
  }, [viewerIsAdmin, refreshKey]);

  const visible = (instances ?? []).filter((x) => typeFilter === 'status' || x.type === typeFilter);

  return (
    <Page>
      <Tabs value={typeFilter} onValueChange={setTypeFilter} className="flex flex-col gap-3">
        {viewerIsAdmin && (
          <QueryBar>
            <TabsList>
              {['status', ...MQ_TYPES].map((type) => (
                <TabsTrigger key={type} value={type}>
                  {type === 'status' ? t('settings.mqStatus') : type === 'kafka' ? 'Kafka' : type === 'rabbitmq' ? 'RabbitMQ' : 'Pulsar'}
                  <span className="ml-1.5 rounded-sm bg-surface-2 px-1.5 font-mono text-xs text-muted">
                    {(instances ?? []).filter((instance) => type === 'status' || instance.type === type).length}
                  </span>
                </TabsTrigger>
              ))}
            </TabsList>
            <Button variant="primary" icon={<Plus className="size-3.5" aria-hidden />} onClick={() => setEditor({ open: true, instance: newInstance(typeFilter === 'status' ? 'kafka' : typeFilter) })}>
              {t('settings.mqNew')}
            </Button>
          </QueryBar>
        )}
        {!viewerIsAdmin && (
          <div className="rounded-md border border-info/30 bg-info-soft px-3 py-2 text-sm text-info">{t('settings.adminOnly')}</div>
        )}
        <TabsContent value={typeFilter} className="flex flex-col gap-3">
        {viewerIsAdmin && !instances && <div className="text-sm text-muted">{t('common.loading')}</div>}
        {viewerIsAdmin && instances && typeFilter === 'status' && <ConsumptionStatus instances={instances} runtime={runtime} failed={statusFailed} sampledAt={sampledAt} error={statusError} writerFailure={writerFailure} notifications={notifications} onRetry={() => setRefreshKey(key => key + 1)} />}
        {viewerIsAdmin && instances && typeFilter !== 'status' && visible.length === 0 && <Empty description={t('settings.mqNoInstances')} />}
        {viewerIsAdmin &&
          instances && typeFilter !== 'status' &&
          visible.map((instance) => (
            <div key={instance.id} className="rounded-lg border border-border bg-surface">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3">
                <div className="flex min-w-0 items-center gap-2">
                  <Tag>{instance.type === 'kafka' ? 'Kafka' : instance.type === 'rabbitmq' ? 'RabbitMQ' : 'Pulsar'}</Tag>
                  <span className="truncate text-sm font-medium text-fg">{instance.name}</span>
                  {!instance.enabled && <Tag>{t('settings.mqDisabled')}</Tag>}
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <Switch
                    checked={instance.enabled}
                    onChange={(checked) => void persist(instances.map((x) => (x.id === instance.id ? { ...x, enabled: checked } : x)))}
                    aria-label={t('settings.mqEnabled')}
                  />
                  <Button
                    variant="link"
                    size="sm"
                    icon={<Pencil className="size-3.5" aria-hidden />}
                    aria-label={`${t('settings.mqEdit')} ${instance.name}`}
                    onClick={() => setEditor({ open: true, instance })}
                  >
                    {t('settings.mqEdit')}
                  </Button>
                  <Popconfirm
                    title={t('common.confirmDelete')}
                    okText={t('common.delete')}
                    cancelText={t('common.cancel')}
                    onConfirm={() => void persist(instances.filter((x) => x.id !== instance.id))}
                  >
                    <Button variant="link" size="sm" danger icon={<Trash2 className="size-3.5" aria-hidden />}>{t('common.delete')}</Button>
                  </Popconfirm>
                </div>
              </div>
              <div className="space-y-3 px-4 py-3">
                {runtime.filter((item) => item.instanceId === instance.id).map((item) => (
                  <div key={item.instanceId} className="flex flex-wrap items-center gap-x-5 gap-y-2 text-xs text-muted">
                    <Tag tone={item.state === 'error' ? 'error' : item.state === 'consuming' ? 'success' : undefined}>{t('settings.mqState.' + item.state)}</Tag>
                    <ConfigValue label={t('settings.mqPersisted')} value={String(item.persistedRecords)} />
                    {item.lastPersistedAt && <ConfigValue label={t('settings.mqLastPersisted')} value={formatDateTime(item.lastPersistedAt)} />}
                    {item.lastError && <span className="w-full break-all rounded-md bg-error-soft px-3 py-2 text-error">{item.lastError}</span>}
                  </div>
                ))}
                <div className="flex min-w-0 flex-wrap gap-x-5 gap-y-1">
                  {instance.type === 'kafka' ? (
                    <>
                      <ConfigValue label={t('settings.mqServers')} value={instance.bootstrapServers} />
                      <ConfigValue label={t('settings.mqGroupId')} value={instance.groupId} />
                      <ConfigValue label={t('settings.mqOffset')} value={instance.autoOffsetReset} />
                    </>
                  ) : instance.type === 'rabbitmq' ? (
                    <ConfigValue label={t('settings.mqAmqpUri')} value={instance.uri.replace(/\/\/[^@/]+@/, '//***@')} />
                  ) : (
                    <>
                      <ConfigValue label={t('settings.mqServiceUrl')} value={instance.serviceUrl} />
                      <ConfigValue label={t('settings.mqSubscription')} value={instance.subscriptionName} />
                      <ConfigValue label={t('settings.mqSubscriptionType')} value={instance.subscriptionType} />
                    </>
                  )}
                </div>
                <div className="overflow-hidden rounded-md border border-border">
                  <div className="flex items-center gap-2 bg-surface-2 px-3 py-2 text-xs font-medium text-muted">
                    {t('settings.mqTopics')} <span className="font-mono">{instance.topics.length}</span>
                  </div>
                  {instance.topics.map((topic, index) => (
                    <div key={`${topic.matchMode ?? 'exact'}:${topic.name}:${index}`} className="border-t border-border px-3 py-2.5">
                      <div className="space-y-2">
                        <TopicSummary topic={topic} />
                        <div className="flex flex-wrap items-center gap-2">
                          <TopicConsumption topic={topicRuntime(runtime.find(item => item.instanceId === instance.id), topic)} enabled={instance.enabled} failed={statusFailed} />
                          {topic.matchMode === 'prefix' && <span className="text-xs text-muted">{t('settings.mqMatchedCount', { count: topicRuntime(runtime.find(item => item.instanceId === instance.id), topic)?.matchedTopics ?? 0 })}</span>}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          ))}
        </TabsContent>
      </Tabs>
      <InstanceDrawer
        open={editor.open}
        instance={editor.instance}
        saving={saving}
        onClose={() => setEditor({ open: false, instance: null })}
        onSave={(next) => {
          const nextList = next.id
            ? (instances ?? []).map((x) => (x.id === next.id ? next : x))
            : [...(instances ?? []), next];
          void persist(nextList);
        }}
      />
    </Page>
  );
}
