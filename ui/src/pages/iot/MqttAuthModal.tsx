import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { fetchMqttAuthSettings, saveMqttAuthSettings } from '@/api/settings';
import type { MqttAuthSettings, SaveMqttAuthSettings } from '@/api/types';
import { Button, Field, Input, Modal, Select } from '@/design-system';

export function MqttAuthModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const [stored, setStored] = useState<MqttAuthSettings | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const { register, watch, setValue, reset, handleSubmit, formState: { errors } } = useForm<SaveMqttAuthSettings>({
    defaultValues: { mode: 'none', key: '' },
  });
  const mode = watch('mode');
  useEffect(() => {
    let cancelled = false;
    void fetchMqttAuthSettings().then(value => {
      if (cancelled) return;
      setStored(value);
      reset({ mode: value.mode === 'invalid' ? 'none' : value.mode, key: '' });
      if (value.mode === 'invalid') setError(t('iot.auth.invalid'));
    }).catch(reason => {
      if (!cancelled) setError(reason instanceof Error ? reason.message : t('iot.auth.error'));
    }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [reset, t]);
  const submit = handleSubmit(async values => {
    setSaving(true); setError('');
    try {
      await saveMqttAuthSettings({ mode: values.mode, key: values.mode === 'none' ? '' : values.key });
      onClose();
    } catch (reason) { setError(reason instanceof Error ? reason.message : t('iot.auth.error')); }
    finally { setSaving(false); }
  });
  return <Modal open onOpenChange={open => { if (!open) onClose(); }} title={t('iot.auth.title')}
    footer={<><Button onClick={onClose}>{t('common.cancel')}</Button><Button variant="primary" disabled={loading || saving || !stored} onClick={() => void submit()}>{t('common.save')}</Button></>}>
    <form onSubmit={event => { event.preventDefault(); void submit(); }} className="space-y-3">
      {error && <p role="alert" className="text-error">{error}</p>}
      <Field label={t('iot.auth.mode')}>
        <Select aria-label={t('iot.auth.mode')} value={mode} disabled={loading || saving}
          options={(['none', 'shared', 'prefix'] as const).map(value => ({ value, label: t('iot.auth.' + value) }))}
          onValueChange={value => { setValue('mode', value as SaveMqttAuthSettings['mode']); setValue('key', ''); }} />
      </Field>
      {mode !== 'none' && <Field label={t(mode === 'prefix' ? 'iot.auth.prefixKey' : 'iot.auth.key')} error={errors.key?.message}>
        <Input type="password" autoComplete="new-password" maxLength={256} disabled={loading || saving} aria-label={t(mode === 'prefix' ? 'iot.auth.prefixKey' : 'iot.auth.key')}
          {...register('key', { validate: value => value.length === 0 && stored?.mode === mode && stored.hasKey || value.trim().length > 0 || t('iot.auth.required'), maxLength: 256 })} />
        {stored?.hasKey && stored.mode === mode && <p className="mt-1 text-xs text-muted">{t('iot.auth.keep')}</p>}
      </Field>}
    </form>
  </Modal>;
}
