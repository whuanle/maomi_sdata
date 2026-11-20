import { useCallback, useEffect, useState } from 'react';
import { Bell, Pencil, Plus, Power, Send, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useForm } from 'react-hook-form';
import { deleteNotificationChannel, fetchNotificationChannels, saveNotificationChannel, testNotificationChannel } from '@/api/settings';
import type { NotificationChannel, SaveNotificationChannel } from '@/api/types';
import { AuditCell, Button, Drawer, Field, Input, Password, Popconfirm, Select, Switch, Textarea, toast } from '@/design-system';

export const CHANNEL_TYPES = [
  { type: 'telegram', name: 'Telegram Bot', fields: ['token', 'chatId'] },
  { type: 'discord', name: 'Discord Webhook', fields: ['webhook'] },
  { type: 'feishu', name: 'Feishu Bot', fields: ['webhook', 'secret'] },
  { type: 'dingtalk', name: 'DingTalk Bot', fields: ['webhook', 'secret'] },
  { type: 'wecom', name: 'WeCom Bot', fields: ['webhook'] },
  { type: 'bark', name: 'Bark', fields: ['server', 'token'] },
  { type: 'gotify', name: 'Gotify', fields: ['server', 'token'] },
  { type: 'serverchan', name: 'Server Chan', fields: ['token'] },
  { type: 'pushdeer', name: 'PushDeer', fields: ['server', 'token'] },
  { type: 'matrix', name: 'Matrix', fields: ['server', 'token', 'roomId'] },
  { type: 'webhook', name: 'Custom JSON Webhook', fields: ['webhook', 'headers', 'template'] },
] as const;
const secretField = (field: string) => ['token', 'secret', 'webhook', 'headers'].includes(field);

interface ChannelForm {
  name: string;
  enabled: boolean;
  config: Record<string, string>;
  actions: Record<string, string>;
}

/** 管理员通知渠道；编辑草稿不影响已保存实例，测试明确确认后真实发送. */
export function NotificationChannelsPanel() {
  const { t } = useTranslation();
  const [items, setItems] = useState<NotificationChannel[]>([]);
  const [loading, setLoading] = useState(true);
  const [target, setTarget] = useState<NotificationChannel | null>(null);
  const [type, setType] = useState('');
  const [pending, setPending] = useState(false);
  const [loadError, setLoadError] = useState(false);
  const { register, reset, watch, setValue, handleSubmit, formState: { errors } } = useForm<ChannelForm>();
  const selected = CHANNEL_TYPES.find(item => item.type === type);

  const load = useCallback(async () => {
    setLoading(true);
    try { setItems(await fetchNotificationChannels()); setLoadError(false); }
    catch { setLoadError(true); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const open = (nextType: string, item: NotificationChannel | null = null) => {
    setTarget(item);
    setType(nextType);
    reset({
      name: item?.name ?? '',
      enabled: item?.enabled ?? true,
      config: { ...(item?.config ?? {}), ...(!item && nextType === 'bark' ? { server: 'https://api.day.app' } : {}), ...(!item && nextType === 'pushdeer' ? { server: 'https://api2.pushdeer.com' } : {}) },
      actions: Object.fromEntries((item?.configuredSecrets ?? []).map(field => [field, 'keep'])),
    });
  };

  const payload = (values: ChannelForm): SaveNotificationChannel => {
    const config: Record<string, string> = {};
    const keepSecrets: string[] = [];
    for (const field of selected?.fields ?? []) {
      if (secretField(field) && values.actions[field] === 'keep') keepSecrets.push(field);
      else if (values.actions[field] !== 'clear' && values.config[field]) config[field] = values.config[field];
    }
    return { id: target?.id, version: target?.version ?? 0, name: values.name.trim(), type, enabled: values.enabled, config, keepSecrets };
  };

  const run = async (action: () => Promise<void>) => {
    setPending(true);
    try { await action(); }
    catch (error) { toast.error(error instanceof Error ? error.message : t('notifications.failed')); }
    finally { setPending(false); }
  };
  const save = handleSubmit(values => run(async () => {
    await saveNotificationChannel(payload(values));
    setType('');
    toast.success(t('notifications.saved'));
    await load();
  }));
  const test = handleSubmit(values => run(async () => {
    const result = await testNotificationChannel(payload(values));
    if (result.success) toast.success(t('notifications.sent'));
    else toast.error(result.message);
  }));
  const toggle = (item: NotificationChannel) => run(async () => {
    await saveNotificationChannel({ id: item.id, version: item.version, name: item.name, type: item.type, enabled: !item.enabled, config: item.config, keepSecrets: item.configuredSecrets });
    await load();
  });

  return <div className="space-y-5">
    <p className="text-sm text-muted">{t('notifications.description')}</p>
    {loading && <p className="text-sm text-muted">{t('common.loading')}</p>}
    {loadError && <p role="alert" className="text-sm text-error">{t('notifications.failed')}</p>}
    <div className="flex items-center gap-2 text-sm font-semibold text-fg"><Bell className="size-4" />{t('notifications.available')}</div>
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
      {CHANNEL_TYPES.map(channel => <div key={channel.type} data-testid={`notification-type-${channel.type}`} className="flex flex-col gap-3 rounded-md border border-border bg-surface p-4">
        <div className="font-semibold text-fg">{channel.name}</div>
        <p className="flex-1 text-sm text-muted">{t('notifications.descriptions.' + channel.type)}</p>
        <ul className="divide-y divide-border">
        {items.filter(item => item.type === channel.type).map(item => <li key={item.id} data-testid={`notification-instance-${item.id}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-1.5">
          <span className="min-w-0 flex-1 truncate text-sm font-medium text-fg" title={item.name}>{item.name}</span>
          <span className={item.enabled ? 'text-xs text-success' : 'text-xs text-muted'}>{t(item.enabled ? 'notifications.enabled' : 'notifications.disabled')}</span>
          <div className="flex shrink-0 flex-wrap gap-1">
            <Button variant="link" size="sm" disabled={pending} icon={<Pencil className="size-3.5" />} onClick={() => open(item.type, item)}>{t('common.edit')}</Button>
            <Button variant="link" size="sm" disabled={pending} icon={<Power className="size-3.5" />} onClick={() => void toggle(item)}>{t(item.enabled ? 'notifications.disable' : 'notifications.enable')}</Button>
            <Popconfirm title={t('common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => run(async () => { await deleteNotificationChannel(item.id, item.version); await load(); })}>
              <Button variant="link" size="sm" danger disabled={pending} icon={<Trash2 className="size-3.5" />}>{t('common.delete')}</Button>
            </Popconfirm>
          </div>
        </li>)}
        </ul>
        <Button icon={<Plus className="size-4" />} onClick={() => open(channel.type)}>{t('notifications.createInstance')}</Button>
      </div>)}
    </div>
    <Drawer open={!!selected} onOpenChange={value => { if (!value) setType(''); }} title={selected?.name} footer={<>
      <Popconfirm title={t('notifications.testConfirm')} okText={t('notifications.test')} cancelText={t('common.cancel')} onConfirm={() => test()}>
        <Button disabled={pending} icon={<Send className="size-4" />}>{t('notifications.test')}</Button>
      </Popconfirm>
      <Button loading={pending} onClick={() => void save()}>{t('common.save')}</Button>
    </>}>
      <form className="space-y-4" onSubmit={save}>
        {target && <div className="grid grid-cols-2 gap-3 border-b border-border pb-3 text-subtle">
          <div><div className="mb-1 text-xs">{t('common.createTime')}</div><AuditCell time={target.createTime} user={target.createUserId} /></div>
          <div><div className="mb-1 text-xs">{t('common.updateTime')}</div><AuditCell time={target.updateTime} user={target.updateUserId} /></div>
        </div>}
        <Field label={t('notifications.name')} required error={errors.name?.message}>
          <Input {...register('name', { required: t('notifications.required'), maxLength: 64, validate: value => !!value.trim() || t('notifications.required') })} />
        </Field>
        <Field label={t('notifications.enabled')}><Switch checked={watch('enabled') ?? false} onChange={value => setValue('enabled', value)} /></Field>
        {selected?.fields.map(field => {
          const secret = secretField(field);
          const configured = target?.configuredSecrets.includes(field);
          const action = watch(`actions.${field}` as const) ?? 'replace';
          const required = !['secret', 'headers', 'template'].includes(field);
          const rules = { validate: (value: string) => !required || (secret && action === 'keep') || !!value?.trim() || t('notifications.required') };
          return <Field key={field} label={t('notifications.fields.' + field)} required={required} error={errors.config?.[field]?.message}>
            {secret && configured && <div className="mb-2"><Select value={action} onValueChange={value => { setValue(`actions.${field}` as const, value); setValue(`config.${field}` as const, ''); }} options={['keep', 'replace', 'clear'].map(value => ({ value, label: t('notifications.' + value) }))} /></div>}
            {(!configured || action === 'replace') && (field === 'template' || field === 'headers'
              ? <Textarea rows={5} {...register(`config.${field}` as const, rules)} />
              : secret ? <Password autoComplete="new-password" {...register(`config.${field}` as const, rules)} />
                : <Input {...register(`config.${field}` as const, rules)} />)}
          </Field>;
        })}
        {type === 'webhook' && <p className="text-xs text-muted">{t('notifications.templateHint')}</p>}
        <p className="text-xs text-muted">{t('notifications.securityHint')}</p>
      </form>
    </Drawer>
  </div>;
}
