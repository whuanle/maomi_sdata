import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { ArrowDown, ArrowUp, Eye, Save, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { DashboardConfig, PanelConfig } from '@/api/types';
import { Button, Checkbox, Field, Input, Modal } from '@/design-system';

export function GroupEditor({ config, initial, onClose, onApply, onPreview }: {
  config: DashboardConfig; initial?: PanelConfig; onClose: () => void; onApply: (group: PanelConfig) => void; onPreview?: () => void;
}) {
  const { t } = useTranslation();
  const { register, watch, handleSubmit, formState: { errors } } = useForm({ defaultValues: { title: initial?.title ?? '' } });
  const title = watch('title');
  const [members, setMembers] = useState(initial?.panels ?? []);
  const defaultId = initial?.defaultPanelId;
  const available = [...(initial?.panels ?? []), ...config.panels.filter(panel => panel.type !== 'group')];
  const move = (index: number, direction: number) => {
    const next = [...members];
    const target = index + direction;
    if (target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target], next[index]];
    setMembers(next);
  };
  return <Modal open onOpenChange={open => { if (!open) onClose(); }} title={t('dashboard.manageGroup')} footer={<>
    <Button icon={<X className="size-4" />} onClick={onClose}>{t('common.cancel')}</Button>
    {onPreview && <Button icon={<Eye className="size-4" />} onClick={onPreview}>{t('dashboard.preview')}</Button>}
    <Button variant="primary" icon={<Save className="size-4" />} disabled={!title.trim()} onClick={handleSubmit(values => onApply({
      ...initial, id: initial?.id ?? crypto.randomUUID(), type: 'group', title: values.title.trim(), panels: members,
      defaultPanelId: members.some(panel => panel.id === defaultId) ? defaultId : members[0]?.id,
    }))}>{t('common.save')}</Button>
  </>}>
    <div className="flex flex-col gap-3">
      <Field label={t('dashboard.groupName')} required error={errors.title?.message}><Input aria-label={t('dashboard.groupName')} maxLength={128} {...register('title', { required: t('dashboard.panelTitleRequired'), validate: value => Boolean(value.trim()) || t('dashboard.panelTitleRequired') })} /></Field>
      {!initial && <p className="text-sm text-muted">{t('dashboard.emptyGroupHint')}</p>}
      {initial && <>
      <p className="text-sm text-muted">{t('dashboard.groupHint')}</p>
      {available.map(panel => <Checkbox key={panel.id} checked={members.some(member => member.id === panel.id)} onChange={event => {
        setMembers(event.target.checked ? [...members, panel] : members.filter(member => member.id !== panel.id));
      }} label={panel.title} />)}
      {members.map((panel, index) => <div key={panel.id} className="flex items-center gap-2">
        <span className="min-w-0 flex-1 truncate">{panel.title}</span>
        <Button variant="link" size="sm" disabled={index === 0} icon={<ArrowUp className="size-3" />} onClick={() => move(index, -1)}>{t('dashboard.moveUp')}</Button>
        <Button variant="link" size="sm" disabled={index === members.length - 1} icon={<ArrowDown className="size-3" />} onClick={() => move(index, 1)}>{t('dashboard.moveDown')}</Button>
      </div>)}
      </>}
    </div>
  </Modal>;
}
