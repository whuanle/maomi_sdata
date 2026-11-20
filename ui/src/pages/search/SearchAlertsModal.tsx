import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Bell, Plus, Save, Trash2, Play, Pencil } from 'lucide-react';
import { alertChannels, alertHistory, deleteAlert, listAlerts, previewAlert, saveAlert } from '@/api/alerts';
import type { AlertChannelItem, AlertDraft, AlertHistory, AlertItem, SavedSearchDefinition } from '@/api/types';
import { AuditCell, Button, Chart, Checkbox, Field, Input, Modal, Popconfirm, Select, Switch, Textarea, toast } from '@/design-system';
import { effectiveSearch } from './effectiveSearch';
import { formatDateTime } from '@/utils/format';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  definition: SavedSearchDefinition;
  savedQueryId?: string;
  canWrite: boolean;
}

export function SearchAlertsModal({ open, onOpenChange, definition, savedQueryId, canWrite }: Props) {
  const { t } = useTranslation();
  const [rules, setRules] = useState<AlertItem[]>([]);
  const [channels, setChannels] = useState<AlertChannelItem[]>([]);
  const [history, setHistory] = useState<AlertHistory>({ points: [], deliveries: [] });
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const { register, watch, setValue, reset, handleSubmit, formState: { errors } } = useForm<AlertDraft>();
  const values = watch();
  const load = async () => setRules(await listAlerts(savedQueryId));
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setEditing(false);
    setHistory({ points: [], deliveries: [] });
    Promise.all([listAlerts(savedQueryId), canWrite ? alertChannels() : Promise.resolve([])]).then(([items, choices]) => {
      if (!cancelled) { setRules(items); setChannels(choices); }
    }).catch(error => toast.error(String(error)));
    return () => { cancelled = true; };
  }, [open, savedQueryId, canWrite]);
  const create = () => {
    try {
      const effective = effectiveSearch(definition);
      reset({ version: 0, savedQueryId, name: '', domain: definition.domain, definition: JSON.stringify(definition),
        ...effective, channels: [], note: '', operator: 'gte', threshold: 1, windowMinutes: 5,
        delayMinutes: 1, consecutiveWindows: 1, enabled: true });
      setHistory({ points: [], deliveries: [] });
      setEditing(true);
    } catch (error) { toast.error(String(error)); }
  };
  const edit = async (rule: AlertItem) => {
    reset(rule);
    setEditing(true);
    setHistory({ points: [], deliveries: [] });
    try { setHistory(await alertHistory(rule.id)); } catch (error) { toast.error(String(error)); }
  };
  const submit = handleSubmit(async draft => {
    setBusy(true);
    try { await saveAlert(draft); await load(); setEditing(false); toast.success(t('alerts.saved')); }
    catch (error) { toast.error(String(error)); }
    finally { setBusy(false); }
  });
  const preview = async () => {
    setBusy(true);
    try {
      const point = await previewAlert(values);
      setHistory({ points: [point], deliveries: [] });
    } catch (error) { toast.error(String(error)); }
    finally { setBusy(false); }
  };
  return <Modal open={open} onOpenChange={onOpenChange} title={t('alerts.title')} width={780} footer={null}>
    <div className="flex max-h-[75vh] flex-col gap-4 overflow-auto">
      <p className="text-xs text-muted">{t('alerts.performance')}</p>
      {!editing ? <>
        {canWrite && <Button icon={<Plus className="size-4" />} onClick={create}>{t('alerts.new')}</Button>}
        {rules.length === 0 && <p className="text-sm text-muted">{t('alerts.empty')}</p>}
        {rules.map(rule => <div key={rule.id} className="flex flex-wrap items-center gap-3 border-b border-border py-2">
          <div className="min-w-0 flex-1"><div>{rule.name}</div><div className="text-xs text-muted">{t('alerts.status.' + rule.status)}{rule.error && ' · ' + rule.error}</div></div>
          <AuditCell time={rule.updateTime} user={rule.updateUserId} />
          <Button variant="link" size="sm" icon={<Pencil className="size-3" />} onClick={() => void edit(rule)}>{t(canWrite ? 'common.edit' : 'alerts.history')}</Button>
          {canWrite && <Popconfirm title={t('common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')}
            onConfirm={async () => { try { await deleteAlert(rule.id, rule.version); await load(); } catch (error) { toast.error(String(error)); } }}>
            <Button variant="link" size="sm" danger icon={<Trash2 className="size-3" />}>{t('common.delete')}</Button>
          </Popconfirm>}
        </div>)}
      </> : <>
        <Field label={t('alerts.name')} required error={errors.name?.message}>
          <Input aria-label={t('alerts.name')} disabled={!canWrite} {...register('name', { required: t('alerts.required'), maxLength: 64 })} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('alerts.trigger')}>
            <Select disabled={!canWrite} value={values.operator} onValueChange={value => setValue('operator', value as AlertDraft['operator'])}
              options={['gte', 'gt', 'lte', 'lt'].map((value, index) => ({ value, label: ['≥', '>', '≤', '<'][index] }))} />
          </Field>
          <Field label={t('alerts.count')} error={errors.threshold?.message}><Input type="number" disabled={!canWrite}
            {...register('threshold', { valueAsNumber: true, required: true, min: 0, max: 1000000 })} /></Field>
          <Field label={t('alerts.window')} error={errors.windowMinutes?.message}><Input type="number" disabled={!canWrite}
            {...register('windowMinutes', { valueAsNumber: true, required: true, min: 1, max: 60 })} /></Field>
          <Field label={t('alerts.consecutive')} error={errors.consecutiveWindows?.message}><Input type="number" disabled={!canWrite}
            {...register('consecutiveWindows', { valueAsNumber: true, required: true, min: 1, max: 10 })} /></Field>
          <Field label={t('alerts.delay')}><Input type="number" disabled={!canWrite}
            {...register('delayMinutes', { valueAsNumber: true, required: true, min: 0, max: 10 })} /></Field>
          <Field label={t('alerts.enabled')}><Switch checked={values.enabled ?? false} disabled={!canWrite} onChange={value => setValue('enabled', value)} /></Field>
        </div>
        <Field label={t('alerts.channels')} required>
          {channels.map(channel => <Checkbox key={channel.id} label={channel.name + ' · ' + channel.type}
            disabled={!canWrite || !channel.enabled} checked={values.channels?.includes(channel.id) ?? false}
            onChange={event => setValue('channels', event.target.checked ? [...(values.channels ?? []), channel.id] : values.channels.filter(id => id !== channel.id))} />)}
          {!channels.length && <p className="text-xs text-muted">{t('alerts.noChannels')}</p>}
        </Field>
        <Field label={t('alerts.note')}><Textarea disabled={!canWrite} {...register('note', { maxLength: 2048 })} /></Field>
        <p className="text-xs text-muted">{t('alerts.snapshot')}</p>
        {canWrite && <Button variant="link" size="sm" icon={<Bell className="size-3" />} onClick={() => {
          try { const effective = effectiveSearch(definition); setValue('condition', effective.condition); setValue('parameters', effective.parameters); setValue('domain', definition.domain); setValue('definition', JSON.stringify(definition)); }
          catch (error) { toast.error(String(error)); }
        }}>{t('alerts.useCurrent')}</Button>}
        <details><summary className="cursor-pointer text-xs text-muted">{t('alerts.condition')}</summary><pre className="whitespace-pre-wrap break-all text-xs">{values.condition || '*'}</pre></details>
        {canWrite && <Button icon={<Play className="size-3" />} disabled={busy} onClick={() => void preview()}>{t('alerts.preview')}</Button>}
        <div className="text-xs text-muted">{t('alerts.trendHint')}</div>
        {history.points.length > 0 && <Chart minHeight={180} option={{
          tooltip: { trigger: 'axis' }, xAxis: { type: 'category', data: history.points.map(point => formatDateTime(point.time)) },
          yAxis: { type: 'value' }, series: [{ type: 'line', data: history.points.map(point => point.status === 'error' ? null : point.count),
            markLine: { data: [{ yAxis: Number(values.threshold) || 0 }] } }],
        }} />}
        {history.points.map(point => <div key={point.time} className="text-xs text-muted">{formatDateTime(point.time)} · {point.error || (point.lowerBound ? '≥' : '') + point.count}</div>)}
        {history.deliveries.map((delivery, index) => <div key={index} className="text-xs text-muted">{delivery.channelId} · {delivery.status} · {delivery.result}</div>)}
        <p className="text-xs text-muted">{t('alerts.sendConsent')}</p>
        <div className="flex gap-2">
          <Button onClick={() => setEditing(false)}>{t('common.cancel')}</Button>
          {canWrite && <Button icon={<Save className="size-4" />} loading={busy} disabled={!values.channels?.length} onClick={() => void submit()}>{t('alerts.save')}</Button>}
        </div>
      </>}
    </div>
  </Modal>;
}
