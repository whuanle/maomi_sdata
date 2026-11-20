import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Copy, Plus, Settings, Save } from 'lucide-react';
import { Button, Field, Input, Textarea, Modal, toast } from '@/design-system';
import { isAdminRole, useAuthStore } from '@/store/auth';
import { saveRumProject } from '@/api/rum';
import type { RumProjectItem } from '@/api/types';
import { displayOrigins, parseOrigins } from './rumOrigins';

interface ProjectForm {
  projectKey: string;
  name: string;
  origins: string;
  perfSampleRate: number;
  replaySampleRate: number;
}

/** 接入帮助与管理员创建入口。 */
export function SessionSetup({ projectKey, project, onCreated }: { projectKey: string; project?: RumProjectItem; onCreated: (key: string) => void }) {
  const { t } = useTranslation();
  const role = useAuthStore((state) => state.role);
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const { register, handleSubmit, reset, formState: { errors, isSubmitting } } = useForm<ProjectForm>({
    defaultValues: { projectKey: 'rum_', name: '', origins: '', perfSampleRate: 1, replaySampleRate: 1 },
  });
  const origin = window.location.origin;
  const snippet = `<script src="${origin}/rum/probe.js"></script>\n<script>\nSDataRum.init({ serverUrl: ${JSON.stringify(origin)}, projectKey: ${JSON.stringify(projectKey || 'rum_demo')} });\n</script>`;
  const submit = handleSubmit(async ({ origins, ...values }) => {
    const parsed = parseOrigins(origins);
    if (!parsed) return;
    try {
      await saveRumProject({ ...values, ...(creating ? {} : { id: project?.id }), domainsJson: JSON.stringify(parsed) });
      setCreating(false);
      setOpen(false);
      onCreated(values.projectKey);
    } catch {
      toast.error(t('clientSession.saveFailed'));
    }
  });
  const originsField = <Field label={t('clientSession.allowedOrigins')} htmlFor="session-project-origins" error={errors.origins?.message}>
    <Textarea id="session-project-origins" rows={3} placeholder="https://app.example.com"
      {...register('origins', { validate: (value) => parseOrigins(value) !== null || t('clientSession.originsInvalid') })} />
    <p className="mt-1 text-xs text-subtle">{t('clientSession.originsHint')}</p>
  </Field>;

  return <>
    {isAdminRole(role) && <Button icon={<Plus className="size-3.5" />} onClick={() => { reset({ projectKey: 'rum_', name: '', origins: '', perfSampleRate: 1, replaySampleRate: 1 }); setCreating(true); }}>{t('clientSession.createProject')}</Button>}
    <Button icon={<Settings className="size-3.5" />} onClick={() => {
      if (project) reset({ projectKey: project.projectKey, name: project.name, origins: displayOrigins(project.domainsJson ?? '[]'), perfSampleRate: project.perfSampleRate, replaySampleRate: project.replaySampleRate });
      setOpen(true);
    }}>{t('clientSession.integration')}</Button>
    <Modal open={open} onOpenChange={setOpen} title={t('clientSession.integration')}>
      <p className="mb-3 text-xs text-subtle">{t('clientSession.privacyHint')}</p>
      <pre className="overflow-auto rounded border border-border bg-surface-2 p-3 font-mono text-xs">{snippet}</pre>
      <Button className="mt-3" icon={<Copy className="size-3.5" />} onClick={() => void navigator.clipboard.writeText(snippet).then(() => toast.success(t('clientSession.copied'))).catch(() => toast.error(t('clientSession.copyFailed')))}>{t('clientSession.copySnippet')}</Button>
      {isAdminRole(role) && project && <form className="mt-4 grid gap-3 border-t border-border pt-4" onSubmit={(event) => void submit(event)}>
        {originsField}
        <Button type="submit" disabled={isSubmitting} icon={<Save className="size-3.5" />}>{t('clientSession.saveOrigins')}</Button>
      </form>}
    </Modal>
    {isAdminRole(role) && <Modal open={creating} onOpenChange={setCreating} title={t('clientSession.createProject')}>
      <form className="grid gap-3" onSubmit={(event) => void submit(event)}>
        <Field label={t('clientSession.projectKey')} htmlFor="session-project-key" required error={errors.projectKey?.message}>
          <Input id="session-project-key" {...register('projectKey', { required: t('clientSession.required'), pattern: { value: /^rum_[a-zA-Z0-9_-]{1,32}$/, message: t('clientSession.keyHint') } })} />
        </Field>
        <Field label={t('clientSession.projectName')} htmlFor="session-project-name" required error={errors.name?.message}>
          <Input id="session-project-name" {...register('name', { required: t('clientSession.required'), maxLength: 64 })} />
        </Field>
        {originsField}
        {(['perfSampleRate', 'replaySampleRate'] as const).map((key) => <Field key={key} label={t(`clientSession.${key}`)} htmlFor={key} error={errors[key]?.message}>
          <Input id={key} type="number" min={0} max={1} step={0.1} {...register(key, { valueAsNumber: true, required: t('clientSession.required'), min: { value: 0, message: t('clientSession.rateHint') }, max: { value: 1, message: t('clientSession.rateHint') } })} />
        </Field>)}
        <Button type="submit" disabled={isSubmitting} icon={<Plus className="size-3.5" />}>{t('clientSession.createProject')}</Button>
      </form>
    </Modal>}
  </>;
}
