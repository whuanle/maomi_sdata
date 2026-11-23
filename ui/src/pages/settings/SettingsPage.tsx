import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { Cloud, History, Package, RefreshCw, Send, Settings2, Trash2, Users } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useForm } from 'react-hook-form';
import {
  fetchSettings,
  saveCompressionSettings,
  saveOtlpSettings,
  saveRetentionSettings,
  saveS3Settings,
  testS3Connection,
  S3_SECRET_MASK,
} from '@/api/settings';
import type {
  CompressionSettings,
  OtlpSettings,
  SaveOtlpSettings,
  RetentionSettings,
  S3Settings,
  SettingsResponse,
} from '@/api/types';
import {
  Button,
  Field,
  Input,
  InputNumber,
  Page,
  Password,
  Popconfirm,
  Select,
  Switch,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  toast,
} from '@/design-system';
import { isAdminRole, useAuthStore } from '@/store/auth';
import { MembersPanel } from './MembersPanel';
import { ApiAgentsPanel } from './ApiAgentsPanel';
import { NotificationChannelsPanel } from './NotificationChannelsPanel';
import { StartupConfigPanel } from './StartupConfigPanel';

/** 合法 tab key（members 全员可见；其余仅管理员；MQ 日志源已独立为「日志消费」页；DuckDB 内存等性能参数由系统自动调控）. */
const TAB_KEYS = ['members', 'startup', 's3', 'retention', 'compression', 'otlp', 'notifications', 'agents'] as const;
const ADMIN_ONLY_TABS = new Set<string>(['startup', 's3', 'retention', 'compression', 'otlp', 'notifications', 'agents']);

/** 系统设置页：顶部 tab 导航（成员全员可见 + 管理员设置 tab：通知渠道/S3 冷存储/三段保留/数据压缩/OTLP）；后端授权是权威. */
export function SettingsPage() {
  const { t } = useTranslation();
  const role = useAuthStore((state) => state.role);
  const viewerIsAdmin = isAdminRole(role);
  const [settings, setSettings] = useState<SettingsResponse | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [searchParams, setSearchParams] = useSearchParams();

  const loadSettings = useCallback(async () => {
    setLoadError(false);
    try {
      setSettings(await fetchSettings());
    } catch {
      setLoadError(true);
    }
  }, []);

  useEffect(() => {
    if (viewerIsAdmin) {
      void loadSettings();
    }
  }, [viewerIsAdmin, loadSettings]);

  // tab 深链（?tab=members）；非法或当前角色不可见的 key 回落到成员
  const requestedTab = searchParams.get('tab') ?? '';
  const activeTab = TAB_KEYS.includes(requestedTab as (typeof TAB_KEYS)[number]) && !(ADMIN_ONLY_TABS.has(requestedTab) && !viewerIsAdmin) ? requestedTab : 'members';
  const handleTabChange = (key: string) => {
    setSearchParams(key === 'members' ? {} : { tab: key }, { replace: true });
  };

  const s3Enabled = Boolean(settings?.s3.endpoint.trim() && settings?.s3.bucket.trim());
  const updateS3 = (s3: S3Settings) => {
    setSettings((current) => current ? { ...current, s3 } : current);
  };

  const updateRetention = (retention: RetentionSettings) => {
    setSettings((current) => current ? { ...current, retention } : current);
  };

  return (
    <Page>
      <Tabs value={activeTab} onValueChange={handleTabChange} className="flex flex-col gap-3">
        <TabsList>
          <TabsTrigger value="members" className="inline-flex items-center gap-1.5">
            <Users className="size-3.5" aria-hidden />
            {t('settings.tabMembers')}
          </TabsTrigger>
          {viewerIsAdmin && (
            <>
              <TabsTrigger value="agents" className="inline-flex items-center gap-1.5">
                <Users className="size-3.5" aria-hidden />
                {t('apiAgents.tab')}
              </TabsTrigger>
              <TabsTrigger value="notifications" className="inline-flex items-center gap-1.5">
                <Send className="size-3.5" aria-hidden />
                {t('notifications.tab')}
              </TabsTrigger>
              <TabsTrigger value="startup" className="inline-flex items-center gap-1.5">
                <Settings2 className="size-3.5" aria-hidden />
                {t('settings.tabStartup')}
              </TabsTrigger>
              <TabsTrigger value="s3" className="inline-flex items-center gap-1.5">
                <Cloud className="size-3.5" aria-hidden />
                {t('settings.tabS3')}
              </TabsTrigger>
              <TabsTrigger value="retention" className="inline-flex items-center gap-1.5">
                <History className="size-3.5" aria-hidden />
                {t('settings.tabRetention')}
              </TabsTrigger>
              <TabsTrigger value="compression" className="inline-flex items-center gap-1.5">
                <Package className="size-3.5" aria-hidden />
                {t('settings.tabCompression')}
              </TabsTrigger>
              <TabsTrigger value="otlp" className="inline-flex items-center gap-1.5">
                <Send className="size-3.5" aria-hidden />
                {t('settings.tabOtlp')}
              </TabsTrigger>
            </>
          )}
        </TabsList>

        <TabsContent value="members">
          <MembersPanel />
        </TabsContent>

        {viewerIsAdmin && (
          <TabsContent value="notifications">
            <NotificationChannelsPanel />
          </TabsContent>
        )}

        {viewerIsAdmin && <TabsContent value="agents"><ApiAgentsPanel /></TabsContent>}

        {viewerIsAdmin && !settings && activeTab !== 'members' && activeTab !== 'notifications' && activeTab !== 'agents' && !loadError && (
          <p role="status" className="text-sm text-muted">{t('common.loading')}</p>
        )}
        {viewerIsAdmin && loadError && (
          <div role="alert" className="flex items-center gap-2 text-sm text-error">
            {t('settings.loadFailed')}
            <Button variant="link" size="sm" icon={<RefreshCw className="size-3.5" />} onClick={() => void loadSettings()}>{t('settings.retry')}</Button>
          </div>
        )}

        {viewerIsAdmin && settings && (
          <>
            <TabsContent value="startup">
              <StartupConfigPanel startup={settings.startup} onRetry={() => void loadSettings()} />
            </TabsContent>
            <TabsContent value="s3">
              <S3SettingsCard initial={settings.s3} onSaved={updateS3} onSaveFailed={loadSettings} />
              {s3Enabled && (
                <div className="mt-3">
                  <RetentionCard initial={settings.retention} onSaved={updateRetention} mode="hot" />
                </div>
              )}
            </TabsContent>
            <TabsContent value="retention">
              <RetentionCard initial={settings.retention} onSaved={updateRetention} mode={s3Enabled ? 'cold' : 'full'} onManageArchive={() => handleTabChange('s3')} />
            </TabsContent>
            <TabsContent value="compression">
              <CompressionCard initial={settings.compression} />
            </TabsContent>
            <TabsContent value="otlp">
              <OtlpCard initial={settings.otlp} />
            </TabsContent>
          </>
        )}
      </Tabs>
    </Page>
  );
}

/** 表单卡片外壳（统一间距与提示行）. */
function FormCard({ title, hint, children, testId }: { title: string; hint?: string; children: ReactNode; testId?: string }) {
  return (
    <section className="rounded-lg border border-border bg-surface p-4" data-testid={testId}>
      <h2 className="text-sm font-semibold text-fg">{title}</h2>
      {hint && <p className="mt-1 text-xs leading-relaxed text-subtle">{hint}</p>}
      <div className="mt-3">{children}</div>
    </section>
  );
}

/** 表单错误消息（ApiError.message 为后端人类可读中文）. */
function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** S3 表单值 → 保存/测试载荷（enabled=false = 关闭冷层只清 endpoint；掩码/空密钥 = 沿用已存值）. */
function toS3Payload(values: { enabled: boolean; endpoint?: string; bucket?: string; prefix?: string; accessKey?: string; secretKey?: string }) {
  return {
    endpoint: values.enabled ? (values.endpoint ?? '').trim() : '',
    bucket: (values.bucket ?? '').trim(),
    prefix: (values.prefix ?? '').trim(),
    accessKey: (values.accessKey ?? '').trim(),
    secretKey: !values.secretKey || values.secretKey === S3_SECRET_MASK ? '' : values.secretKey,
  };
}

interface S3FormValues {
  enabled: boolean;
  endpoint: string;
  bucket: string;
  prefix: string;
  accessKey: string;
  secretKey: string;
}

/** 冷存储 S3 卡片（保存立即重建 SECRET，立即生效）. */
function S3SettingsCard({ initial, onSaved, onSaveFailed }: { initial: S3Settings; onSaved: (settings: S3Settings) => void; onSaveFailed: () => Promise<void> }) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const {
    register,
    handleSubmit,
    watch,
    reset,
    setValue,
    formState: { errors },
  } = useForm<S3FormValues>({
    defaultValues: {
      enabled: initial.endpoint !== '',
      endpoint: initial.endpoint,
      bucket: initial.bucket,
      prefix: initial.prefix,
      accessKey: initial.accessKey,
      secretKey: initial.hasSecret ? S3_SECRET_MASK : '',
    },
  });

  useEffect(() => {
    reset({
      enabled: initial.endpoint !== '',
      endpoint: initial.endpoint,
      bucket: initial.bucket,
      prefix: initial.prefix,
      accessKey: initial.accessKey,
      secretKey: initial.hasSecret ? S3_SECRET_MASK : '',
    });
  }, [initial, reset]);

  const enabled = watch('enabled');
  const busy = saving || testing;

  /** 校验通过后执行动作（保存/测试）；校验失败由 react-hook-form 渲染 Field 错误，API 失败走 toast；无论成败都复位 pending 态. */
  const run = (action: (payload: ReturnType<typeof toS3Payload>) => Promise<unknown>, successTip: string) =>
    handleSubmit(async (values) => {
      try {
        await action(toS3Payload(values));
        toast.success(successTip);
      } catch (error) {
        toast.error(errorMessage(error));
      }
    })().finally(() => {
      setSaving(false);
      setTesting(false);
    });

  return (
    <FormCard title={t('settings.s3')} hint={t('settings.s3Hint')}>
      <div className="grid grid-cols-2 gap-3 xl:grid-cols-[repeat(24,minmax(0,1fr))]">
        <Field label={t('settings.s3Enabled')} className="col-span-1 xl:col-span-4">
          <div className="pt-1.5">
            <Switch
              checked={enabled}
              onChange={(checked) => setValue('enabled', checked)}
              disabled={busy}
              aria-label={t('settings.s3Enabled')}
            />
          </div>
        </Field>
        <Field label={t('settings.s3Endpoint')} required={enabled} error={errors.endpoint?.message} className="xl:col-span-10">
          <Input
            placeholder="http://minio:9000"
            disabled={!enabled || busy}
            invalid={Boolean(errors.endpoint)}
            {...register('endpoint', { required: enabled ? (t('settings.s3Endpoint') ?? '') : false })}
          />
        </Field>
        <Field label={t('settings.s3Bucket')} required={enabled} error={errors.bucket?.message} className="xl:col-span-5">
          <Input
            disabled={!enabled || busy}
            invalid={Boolean(errors.bucket)}
            {...register('bucket', { required: enabled ? (t('settings.s3Bucket') ?? '') : false })}
          />
        </Field>
        <Field label={t('settings.s3Prefix')} className="xl:col-span-5">
          <Input placeholder="sdata/archive" disabled={!enabled || busy} {...register('prefix')} />
        </Field>
        <Field label={t('settings.s3AccessKey')} required={enabled} error={errors.accessKey?.message} className="xl:col-span-10">
          <Input
            disabled={!enabled || busy}
            invalid={Boolean(errors.accessKey)}
            {...register('accessKey', { required: enabled ? (t('settings.s3AccessKey') ?? '') : false })}
          />
        </Field>
        <Field
          label={t('settings.s3SecretKey')}
          error={errors.secretKey?.message}
          help={initial.hasSecret ? t('settings.s3SecretKeepHint') : undefined}
          className="col-span-2 xl:col-span-14"
        >
          <Password
            placeholder={initial.hasSecret ? S3_SECRET_MASK : ''}
            autoComplete="new-password"
            disabled={!enabled || busy}
            {...register('secretKey')}
          />
        </Field>
      </div>
      <div className="mt-3 flex items-center gap-2">
        <Button
          loading={testing}
          disabled={!enabled}
          onClick={() => {
            setTesting(true);
            void run((payload) => testS3Connection(payload).then((res) => {
              if (!res.success) {
                throw new Error(res.message);
              }
            }), t('settings.testSuccess'));
          }}
        >
          {t('settings.s3Test')}
        </Button>
        <Button
          variant="primary"
          loading={saving}
          onClick={() => {
            setSaving(true);
            void run(async (payload) => {
              try {
                onSaved(await saveS3Settings(payload));
              } catch (error) {
                await onSaveFailed();
                throw error;
              }
            }, t('common.save'));
          }}
        >
          {t('common.save')}
        </Button>
      </div>
    </FormCard>
  );
}

/** 保留档位表单值（InputNumber 清空为 NaN，提交时按 0 处理）. */
type RetentionFormValues = Record<keyof RetentionSettings, { hotDays?: number; coldDays?: number }>;

const toRetentionFormValues = (settings: RetentionSettings): RetentionFormValues =>
  Object.fromEntries(
    (Object.keys(settings) as (keyof RetentionSettings)[]).map((domain) => [domain, settings[domain]]),
  ) as RetentionFormValues;

/** 清空/非数值按 0（永久/不转冷）处理. */
const daysOrZero = (value?: number) => (value !== undefined && Number.isFinite(value) ? value : 0);

/** 三段保留卡片（5 域热/冷天数，下一轮保留/归档 tick 生效）. */
function RetentionCard({ initial, onSaved, mode, onManageArchive }: { initial: RetentionSettings; onSaved: (settings: RetentionSettings) => void; mode: 'full' | 'hot' | 'cold'; onManageArchive?: () => void }) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const { register, handleSubmit, reset, formState: { errors } } = useForm<RetentionFormValues>({
    defaultValues: toRetentionFormValues(initial),
  });

  useEffect(() => {
    reset(toRetentionFormValues(initial));
  }, [initial, reset]);

  const onSubmit = handleSubmit(async (values) => {
    try {
      setSaving(true);
      const payload = Object.fromEntries(
        (Object.keys(initial) as (keyof RetentionSettings)[]).map((domain) => [domain, {
          ...initial[domain],
          ...(mode !== 'cold' ? { hotDays: daysOrZero(values[domain]?.hotDays) } : {}),
          ...(mode !== 'hot' ? { coldDays: daysOrZero(values[domain]?.coldDays) } : {}),
        }]),
      ) as unknown as RetentionSettings;
      const saved = await saveRetentionSettings(payload);
      onSaved(saved);
      toast.success(t('common.save'));
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setSaving(false);
    }
  });

  return (
    <FormCard title={t(mode === 'hot' ? 'settings.archivePolicy' : 'settings.retention')} hint={t(mode === 'hot' ? 'settings.archiveHint' : mode === 'cold' ? 'settings.retentionS3Hint' : 'settings.retentionHint')}>
      {mode === 'cold' && (
        <Button variant="link" size="sm" icon={<Cloud className="size-3.5" />} className="mb-3" onClick={onManageArchive}>{t('settings.manageArchive')}</Button>
      )}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        {(Object.keys(initial) as (keyof RetentionSettings)[]).map((domain) => (
          <div key={domain}>
            <div className="text-[13px] font-semibold text-fg">{domain === "rum" ? t("settings.sessionsRetention") : domain}</div>
            {mode !== 'cold' && (
              <Field label={t('settings.retentionHot')} error={errors[domain]?.hotDays?.message} className="mt-1.5">
                <InputNumber aria-label={`${domain} ${t('settings.retentionHot')}`} {...register(`${domain}.hotDays`, { valueAsNumber: true, required: true, min: 0, max: 36500, validate: Number.isInteger })} min={0} max={36500} disabled={saving} />
              </Field>
            )}
            {mode !== 'hot' && (
              <Field label={t('settings.retentionCold')} error={errors[domain]?.coldDays?.message} className="mt-2">
                <InputNumber aria-label={`${domain} ${t('settings.retentionCold')}`} {...register(`${domain}.coldDays`, { valueAsNumber: true, required: true, min: 0, max: 36500, validate: Number.isInteger })} min={0} max={36500} disabled={saving} />
              </Field>
            )}
          </div>
        ))}
      </div>
      <Button variant="primary" loading={saving} className="mt-3" onClick={() => void onSubmit()}>
        {t('common.save')}
      </Button>
    </FormCard>
  );
}

/** 数据压缩卡片（冷存档 parquet 压缩编码，下一轮归档 tick 生效）. */
function CompressionCard({ initial }: { initial: CompressionSettings }) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const { handleSubmit, watch, setValue, reset } = useForm<CompressionSettings>({ defaultValues: initial });

  useEffect(() => {
    reset(initial);
  }, [initial, reset]);

  const codec = watch('archiveParquetCodec');

  const onSubmit = handleSubmit(async (values) => {
    try {
      setSaving(true);
      await saveCompressionSettings(values);
      toast.success(t('common.save'));
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setSaving(false);
    }
  });

  return (
    <FormCard title={t('settings.compression')} hint={t('settings.compressionHint')} testId="settings-compression-card">
      <div className="flex flex-wrap items-end gap-3 py-1">
        <Field label={t('settings.compressionCodec')} className="w-full sm:w-48">
          <Select
            className="w-full"
            value={codec}
            onValueChange={(value) => setValue('archiveParquetCodec', value)}
            options={[
              { value: 'snappy', label: 'snappy' },
              { value: 'zstd', label: 'zstd' },
              { value: 'gzip', label: 'gzip' },
              { value: 'uncompressed', label: 'uncompressed' },
            ]}
          />
        </Field>
        <Button variant="primary" loading={saving} onClick={() => void onSubmit()}>
          {t('common.save')}
        </Button>
      </div>
      <div className="mt-5 grid grid-cols-1 gap-5 border-t border-border pt-4 md:grid-cols-2 md:gap-8">
        <section className="min-w-0">
          <h3 className="text-xs font-medium text-fg">{t('settings.compressionScopeTitle')}</h3>
          <ul className="mt-2 list-disc space-y-2 pl-4 text-xs leading-relaxed text-muted">
            <li>{t('settings.compressionScope')}</li>
            <li>{t('settings.compressionExclusions')}</li>
            <li>{t('settings.compressionTiming')}</li>
            <li>{t('settings.compressionExisting')}</li>
          </ul>
        </section>
        <section className="min-w-0">
          <h3 className="text-xs font-medium text-fg">{t('settings.compressionCodecsTitle')}</h3>
          <dl className="mt-2 space-y-2 text-xs leading-relaxed">
            {(['snappy', 'zstd', 'gzip', 'uncompressed'] as const).map((name) => (
              <div key={name} className="grid grid-cols-[7rem_minmax(0,1fr)] gap-3">
                <dt className="font-mono text-fg">{name}</dt>
                <dd className="text-muted">{t(`settings.compressionCodecs.${name}`)}</dd>
              </div>
            ))}
          </dl>
          <p className="mt-3 text-xs leading-relaxed text-subtle">{t('settings.compressionResultsHint')}</p>
        </section>
      </div>
    </FormCard>
  );
}

/** 自身遥测导出卡片（重启生效）. */
function OtlpCard({ initial }: { initial: OtlpSettings }) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const { register, handleSubmit, watch, setValue, reset } = useForm<SaveOtlpSettings>({ defaultValues: initial });

  useEffect(() => {
    reset(initial);
  }, [initial, reset]);

  const protocol = watch('protocol');

  const onSubmit = handleSubmit(async (values) => {
    try {
      setSaving(true);
      const saved = await saveOtlpSettings(values);
      reset(saved);
      toast.success(t('common.save'));
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setSaving(false);
    }
  });

  return (
    <FormCard title={t('settings.otlp')} hint={t('settings.otlpRestartHint')}>
      <div className="mb-2 text-sm font-medium">{t('settings.otlpExternal')}</div>
      <p className="mb-3 text-xs text-muted">{t('settings.otlpHeadersHint')}</p>
      <div className="mb-3 grid grid-cols-1 gap-3 md:grid-cols-2">
        {(['trace', 'metrics'] as const).map((signal) => {
          const field = signal === 'trace' ? 'traceHeaders' : 'metricsHeaders';
          const clearField = signal === 'trace' ? 'clearTraceHeaders' : 'clearMetricsHeaders';
          return (
            <div key={signal}>
              <Field label={t(signal === 'trace' ? 'settings.otlpTraceHeaders' : 'settings.otlpMetricsHeaders')}>
                <Input type="password" autoComplete="new-password" placeholder="authorization=Bearer token,x-api-key=secret" {...register(field)}
                  onChange={(event) => { setValue(field, event.target.value); setValue(clearField, false); }} />
              </Field>
              <div className="mt-1 flex items-center gap-2 text-xs text-muted">
                <span>{watch(clearField) ? t('settings.otlpHeadersWillClear') : t('settings.otlpHeadersKeep')}</span>
                <Popconfirm title={t('common.confirmDelete')} description={t('settings.otlpHeadersClearConfirm')}
                  okText={t('settings.otlpHeadersClear')} cancelText={t('common.cancel')}
                  onConfirm={() => { setValue(field, ''); setValue(clearField, true); }}>
                  <Button variant="link" size="sm" danger><Trash2 size={14} />{t('settings.otlpHeadersClear')}</Button>
                </Popconfirm>
              </div>
            </div>
          );
        })}
      </div>
      <div className="grid grid-cols-1 gap-3 md:grid-cols-[3fr_3fr_2fr]">
        <Field label={t('settings.otlpTrace')}>
          <Input placeholder="http://localhost:4317" {...register('trace')} />
        </Field>
        <Field label={t('settings.otlpMetrics')}>
          <Input placeholder="http://localhost:4317" {...register('metrics')} />
        </Field>
        <Field label={t('settings.otlpProtocol')}>
          <Select
            className="w-full"
            value={protocol}
            onValueChange={(value) => setValue('protocol', value)}
            options={[{ value: 'grpc', label: 'grpc' }, { value: 'http/protobuf', label: 'http/protobuf' }]}
          />
        </Field>
      </div>
      <Button variant="primary" loading={saving} className="mt-3" onClick={() => void onSubmit()}>
        {t('common.save')}
      </Button>
    </FormCard>
  );
}
