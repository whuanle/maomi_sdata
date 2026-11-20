import { useCallback, useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { Activity, Check, Pencil, Plus, Search, Shield, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { deleteDevice, getDeviceLatest, queryDevices, queryTelemetry, saveDevice } from '@/api/iot';
import type { DeviceItem, DeviceLatestValue, TelemetrySeriesItem } from '@/api/types';
import {
  AuditCell,
  Button,
  Chart,
  DataTable,
  Drawer,
  Field,
  Input,
  Modal,
  Page,
  Popconfirm,
  QueryBar,
  Switch,
  Tag,
  Textarea,
  toast,
  type TableColumn,
} from '@/design-system';
import { canWriteRole, isAdminRole, useAuthStore } from '@/store/auth';
import { formatDateTime } from '@/utils/format';
import { cn } from '@/utils/cn';
import { MqttAuthModal } from './MqttAuthModal';

/** 设备编辑表单值. */
interface DeviceFormValues {
  deviceKey: string;
  name: string;
  group: string;
  tagsJson: string;
  enabled: boolean;
}

/** 搜索表单值. */
interface DeviceSearchValues {
  search: string;
}

/** 新建时的表单默认值（enabled 默认开启，tagsJson 默认空对象）. */
const EMPTY_DEVICE: DeviceFormValues = { deviceKey: '', name: '', group: '', tagsJson: '{}', enabled: true };

/** 遥测时间窗档位：key / 毫秒跨度 / 聚合步长（秒）. */
const TELEMETRY_WINDOWS = [
  { key: '1h', spanMs: 3_600_000, stepSeconds: 60, labelKey: 'iot.win1h' },
  { key: '1d', spanMs: 86_400_000, stepSeconds: 300, labelKey: 'iot.win1d' },
  { key: '3d', spanMs: 3 * 86_400_000, stepSeconds: 900, labelKey: 'iot.win3d' },
  { key: '7d', spanMs: 7 * 86_400_000, stepSeconds: 1800, labelKey: 'iot.win7d' },
] as const;

type TelemetryWindowKey = (typeof TELEMETRY_WINDOWS)[number]['key'];

/** 设备管理页：注册 CRUD + 遥测抽屉（推送主题清单/时间窗切换/曲线按主题过滤）. */
export function DevicesPage() {
  const { t } = useTranslation();
  const canWrite = useAuthStore((state) => canWriteRole(state.role));
  const [data, setData] = useState<DeviceItem[]>([]);
  const [count, setCount] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [editing, setEditing] = useState<DeviceItem | null>(null);
  const isAdmin = useAuthStore(state => isAdminRole(state.role));
  const [authOpen, setAuthOpen] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [telemetryDevice, setTelemetryDevice] = useState<DeviceItem | null>(null);
  const [series, setSeries] = useState<TelemetrySeriesItem[]>([]);
  const [latest, setLatest] = useState<DeviceLatestValue[]>([]);
  const [windowKey, setWindowKey] = useState<TelemetryWindowKey>('1h');
  const [selectedSignals, setSelectedSignals] = useState<Set<string>>(new Set());
  const {
    register,
    reset,
    watch,
    setValue,
    handleSubmit,
    formState: { errors },
  } = useForm<DeviceFormValues>({ defaultValues: EMPTY_DEVICE });
  const { register: registerSearch, getValues } = useForm<DeviceSearchValues>({ defaultValues: { search: '' } });

  const load = useCallback(async (targetPage: number) => {
    setLoading(true);
    try {
      const result = await queryDevices({ page: targetPage, pageSize: 20, search: getValues('search') || undefined });
      setPage(targetPage);
      setData(result.data);
      setCount(result.count);
    } finally {
      setLoading(false);
    }
  }, [getValues]);

  useEffect(() => {
    void load(1);
  }, [load]);

  const loadTelemetry = useCallback(async (deviceKey: string, key: TelemetryWindowKey) => {
    const span = TELEMETRY_WINDOWS.find((item) => item.key === key) ?? TELEMETRY_WINDOWS[0];
    const end = new Date();
    const result = await queryTelemetry({
      deviceKey,
      stepSeconds: span.stepSeconds,
      start: new Date(end.getTime() - span.spanMs).toISOString(),
      end: end.toISOString(),
    });
    setSeries(result.series);
  }, []);

  const openTelemetry = async (device: DeviceItem) => {
    setTelemetryDevice(device);
    setWindowKey('1h');
    const [, latestValues] = await Promise.all([loadTelemetry(device.deviceKey, '1h'), getDeviceLatest(device.deviceKey)]);
    setLatest(latestValues);
    // 默认全选数值信号上曲线；字符串状态量仅列入清单
    setSelectedSignals(new Set(latestValues.filter((item) => item.numValue !== null).map((item) => item.signal)));
  };

  const changeWindow = async (key: TelemetryWindowKey) => {
    if (!telemetryDevice) {
      return;
    }

    setWindowKey(key);
    await loadTelemetry(telemetryDevice.deviceKey, key);
  };

  const toggleSignal = (signal: string) => {
    setSelectedSignals((current) => {
      const next = new Set(current);
      if (next.has(signal)) {
        next.delete(signal);
      } else {
        next.add(signal);
      }

      return next;
    });
  };

  const submitDevice = handleSubmit(async (values) => {
    await saveDevice({
      id: editing?.id ?? 0,
      deviceKey: values.deviceKey,
      name: values.name,
      group: values.group ?? '',
      tagsJson: values.tagsJson || '{}',
      enabled: values.enabled ?? true,
    });
    toast.success(t('common.save'));
    setModalOpen(false);
    await load(page);
  });

  const removeDevice = async (id: number) => {
    await deleteDevice(id);
    toast.success(t('common.delete'));
    await load(page);
  };

  const columns: TableColumn<DeviceItem>[] = [
    { title: t('iot.deviceKey'), dataIndex: 'deviceKey', width: 150, mono: true },
    { title: t('iot.name'), dataIndex: 'name', width: 150 },
    { title: t('iot.group'), dataIndex: 'group', width: 120, render: (value: string) => (value ? <Tag>{value}</Tag> : '-') },
    { title: t('iot.enabled'), dataIndex: 'enabled', width: 80, render: (value: boolean) => (value ? <Tag tone="success">ON</Tag> : <Tag>OFF</Tag>) },
    { title: t('common.createTime'), dataIndex: 'createTime', width: 160, mono: true, render: (value: string) => (value ? formatDateTime(value) : '-') },
    { title: t('common.updateTime'), dataIndex: 'updateTime', width: 160, render: (value: string) => <AuditCell time={value} /> },
    {
      title: t('common.actions'),
      width: 220,
      render: (_, record) => (
        <div className="flex items-center gap-1">
          <Button variant="link" size="sm" icon={<Activity className="size-3" aria-hidden />} onClick={() => void openTelemetry(record)}>
            {t('iot.telemetry')}
          </Button>
          {canWrite && (
            <>
              <Button
                variant="link"
                size="sm"
                icon={<Pencil className="size-3" aria-hidden />}
                onClick={() => {
                  setEditing(record);
                  reset({ deviceKey: record.deviceKey, name: record.name, group: record.group, tagsJson: record.tagsJson, enabled: record.enabled });
                  setModalOpen(true);
                }}
              >
                {t('common.edit')}
              </Button>
              <Popconfirm
                title={t('common.confirmDelete')}
                okText={t('common.delete')}
                cancelText={t('common.cancel')}
                onConfirm={() => void removeDevice(record.id)}
              >
                <Button variant="link" size="sm" danger icon={<Trash2 className="size-3" aria-hidden />}>{t('common.delete')}</Button>
              </Popconfirm>
            </>
          )}
        </div>
      ),
    },
  ];

  const visibleSeries = series.filter((item) => selectedSignals.has(item.signal));
  const telemetryOption = {
    tooltip: { trigger: 'axis' as const },
    legend: { data: visibleSeries.map((item) => item.signal) },
    grid: { left: 8, right: 16, top: 32, bottom: 24, containLabel: true },
    xAxis: { type: 'time' as const },
    yAxis: { type: 'value' as const },
    series: visibleSeries.map((item) => ({
      name: item.signal,
      type: 'line' as const,
      showSymbol: true,
      data: item.points.map((point) => [point.timestampMs, point.value] as [number, number | null]),
    })),
  };

  return (
    <Page>
      <div className="flex flex-col gap-3">
        <QueryBar>
          <Input
            {...registerSearch('search')}
            className="w-[220px]"
            placeholder={t('common.search')}
            testId="device-search"
            onKeyDown={(event) => {
              if (event.key === 'Enter') void load(1);
            }}
          />
          <Button icon={<Search className="size-3.5" aria-hidden />} loading={loading} onClick={() => void load(1)}>
            {t('common.query')}
          </Button>
          {canWrite && (
            <Button
              variant="primary"
              icon={<Plus className="size-3.5" aria-hidden />}
              onClick={() => {
                setEditing(null);
                reset(EMPTY_DEVICE);
                setModalOpen(true);
              }}
            >
              {t('common.create')}
            </Button>
          )}
          {isAdmin && <Button icon={<Shield className="size-3.5" />} onClick={() => setAuthOpen(true)}>{t('iot.auth.title')}</Button>}
        </QueryBar>
        <p className="text-xs text-muted">{t('iot.connectionHint')}</p>
        <DataTable<DeviceItem>
          rowKey="id"
          loading={loading}
          dataSource={data}
          columns={columns}
          pagination={{ current: page, pageSize: 20, total: count, onChange: (target) => { void load(target); } }}
        />
      </div>

      {authOpen && isAdmin && <MqttAuthModal onClose={() => setAuthOpen(false)} />}
      <Modal
        open={modalOpen}
        onOpenChange={(open) => (!open ? setModalOpen(false) : undefined)}
        title={editing ? `${t('common.edit')} · ${editing.deviceKey}` : t('common.create')}
        footer={(
          <>
            <Button onClick={() => setModalOpen(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" onClick={() => void submitDevice()}>{t('common.save')}</Button>
          </>
        )}
      >
        <form onSubmit={(event) => { event.preventDefault(); void submitDevice(); }} className="flex flex-col gap-3">
          <Field label={t('iot.deviceKey')} required error={errors.deviceKey?.message} htmlFor="device-key">
            <Input
              id="device-key"
              invalid={Boolean(errors.deviceKey)}
              disabled={editing !== null}
              {...register('deviceKey', { required: 'a-zA-Z0-9_ - : .', pattern: { value: /^[a-zA-Z0-9_\-:.]{1,64}$/, message: 'a-zA-Z0-9_ - : .' } })}
            />
          </Field>
          <Field label={t('iot.name')} required error={errors.name?.message} htmlFor="device-name">
            <Input id="device-name" invalid={Boolean(errors.name)} {...register('name', { required: t('iot.name') ?? '' })} />
          </Field>
          <Field label={t('iot.group')} htmlFor="device-group">
            <Input id="device-group" {...register('group')} />
          </Field>
          <Field label={t('iot.tags')} error={errors.tagsJson?.message} htmlFor="device-tags">
            <Textarea
              id="device-tags"
              rows={2}
              invalid={Boolean(errors.tagsJson)}
              {...register('tagsJson', {
                validate: (value) => {
                  try {
                    JSON.parse(value || '{}');
                    return true;
                  } catch {
                    return 'invalid json';
                  }
                },
              })}
            />
          </Field>
          <Field label={t('iot.enabled')}>
            <Switch checked={watch('enabled') ?? true} onChange={(checked) => setValue('enabled', checked)} aria-label={t('iot.enabled')} />
          </Field>
        </form>
      </Modal>

      <Drawer
        open={telemetryDevice !== null}
        onOpenChange={(open) => (!open ? setTelemetryDevice(null) : undefined)}
        title={(
          <span className="flex items-center gap-1">
            {t('iot.telemetry')}
            {' · '}
            <code className="rounded-sm bg-surface-2 px-1 py-0.5 font-mono text-[13px]">{telemetryDevice?.deviceKey}</code>
          </span>
        )}
        width={720}
      >
        <h3 className="text-sm font-semibold text-fg">{t('iot.topics')}</h3>
        <p className="mt-1 text-xs text-subtle">{t('iot.topicsHint')}</p>
        <ul className="mt-2 mb-4 flex flex-col gap-1">
          {latest.map((item) => {
            const topic = `iot/${telemetryDevice?.deviceKey ?? ''}/${item.signal}`;
            const value = item.numValue ?? item.strValue ?? '-';
            const time = <span className="w-[150px] shrink-0 text-right text-subtle">{formatDateTime(item.timestampMs)}</span>;
            if (item.numValue === null) {
              // 字符串状态量：不上曲线，仅列入清单
              return (
                <li key={item.signal} className="flex items-center gap-2 rounded-sm px-2 py-1 text-xs text-muted">
                  <span className="size-3 shrink-0" aria-hidden />
                  <code className="flex-1 truncate font-mono text-fg">{topic}</code>
                  <span className="shrink-0 font-mono">{value}</span>
                  <Tag>{t('iot.strSignal')}</Tag>
                  {time}
                </li>
              );
            }

            const selected = selectedSignals.has(item.signal);
            return (
              <li key={item.signal}>
                <button
                  type="button"
                  aria-pressed={selected}
                  onClick={() => toggleSignal(item.signal)}
                  className={cn(
                    'flex w-full items-center gap-2 rounded-sm px-2 py-1 text-left text-xs',
                    selected ? 'bg-primary-soft text-fg' : 'text-muted hover:bg-surface-2',
                  )}
                >
                  <Check aria-hidden className={cn('size-3 shrink-0', selected ? 'text-primary' : 'text-transparent')} />
                  <code className="flex-1 truncate font-mono">{topic}</code>
                  <span className="shrink-0 font-mono">{value}</span>
                  {time}
                </button>
              </li>
            );
          })}
          {latest.length === 0 && <li className="text-sm text-subtle">{t('common.empty')}</li>}
        </ul>
        <div className="flex items-center justify-between">
          <h3 className="text-sm font-semibold text-fg">{t('iot.telemetry')}</h3>
          <div className="flex items-center gap-1">
            {TELEMETRY_WINDOWS.map((span) => (
              <Button
                key={span.key}
                size="sm"
                variant={windowKey === span.key ? 'accentSoft' : 'ghost'}
                aria-pressed={windowKey === span.key}
                onClick={() => void changeWindow(span.key)}
              >
                {t(span.labelKey)}
              </Button>
            ))}
          </div>
        </div>
        <div className="mt-2 h-[320px]">
          <Chart option={telemetryOption} />
        </div>
      </Drawer>
    </Page>
  );
}
