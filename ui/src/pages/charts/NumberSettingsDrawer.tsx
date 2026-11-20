import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowDown, ArrowUp, Check, Plus, RotateCcw, Trash2 } from 'lucide-react';
import { Button, Drawer, Input, Select, Switch } from '@/design-system';
import type { ChartOutputFormat } from '@/api/chartTypes';
import type { NumberColor, NumberDisplaySettings, NumberFormat } from './numberTypes';
import { DEFAULT_NUMBER_SETTINGS, normalizeNumberSettings } from './numberTypes';
import { formatNumberValue } from './numberFormat';

const colors: NumberColor[] = ['default', 'primary', 'accent', 'success', 'warning', 'error'];

function FormatFields({ value, onChange }: { value: NumberFormat; onChange: (value: NumberFormat) => void }) {
  const { t } = useTranslation();
  const units: Partial<Record<ChartOutputFormat, string[]>> = {
    currency: ['USD', 'EUR', 'CNY', 'JPY', 'GBP'], percentage: ['ratio', 'percent'],
    duration: ['s', 'ms', 'us', 'ns'], data: ['binary', 'decimal'], data_rate: ['binary', 'decimal'],
    throughput: ['second', 'minute', 'hour'],
  };
  return <div className="space-y-4">
    <label className="block space-y-2"><span>{t('numberChart.outputFormat')}</span><Select value={value.output} aria-label={t('numberChart.outputFormat')} onValueChange={(output) => onChange({ ...value, output: output as ChartOutputFormat, unit: undefined })} options={(['number', 'currency', 'percentage', 'duration', 'time', 'data', 'data_rate', 'throughput'] as const).map((output) => ({ value: output, label: t(`numberChart.format_${output}`) }))} /></label>
    {value.output === 'throughput' && <p className="text-xs text-muted">{t('numberChart.throughputHint')}</p>}
    {units[value.output] && <label className="block space-y-2"><span>{t('numberChart.unit')}</span><Select value={value.unit ?? units[value.output]![0]} aria-label={t('numberChart.unit')} options={units[value.output]!.map((unit) => ({ value: unit, label: t(`numberChart.units.${unit}`, unit) }))} onValueChange={(unit) => onChange({ ...value, unit })} /></label>}
    <div className="rounded border border-border bg-surface-2 p-3"><span className="text-muted">{t('numberChart.formatExample')}</span><div className="mt-1 font-mono">{formatNumberValue(1234, value)}</div></div>
    <label className="block space-y-2"><span>{t('numberChart.decimals')}: {value.decimals}</span><input type="range" min={0} max={10} value={value.decimals} className="w-full accent-primary" aria-label={t('numberChart.decimals')} onChange={(event) => onChange({ ...value, decimals: Number(event.target.value) })} /><span className="flex justify-between text-xs text-muted"><span>0</span><span>10</span></span></label>
    <label className="flex items-center gap-2"><Switch checked={value.thousands} onChange={(thousands) => onChange({ ...value, thousands })} aria-label={t('numberChart.separateThousands')} />{t('numberChart.separateThousands')}</label>
    <label className="flex items-center gap-2"><Switch checked={value.compact} onChange={(compact) => onChange({ ...value, compact })} aria-label={t('numberChart.largeNumberFormat')} />{t('numberChart.largeNumberFormat')}</label>
  </div>;
}

/** 配置草稿仅在应用时提交，关闭抽屉即放弃. */
export function NumberSettingsDrawer({ settings, override, seriesOnly = false, onApply, onClose }: {
  settings: NumberDisplaySettings; override?: NumberFormat; seriesOnly?: boolean;
  onApply: (settings: NumberDisplaySettings, override?: NumberFormat) => void; onClose: () => void;
}) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState(() => normalizeNumberSettings(settings));
  const [format, setFormat] = useState(override ?? settings.format);
  const [custom, setCustom] = useState(Boolean(override));
  const invalid = draft.rules.some((rule) => !Number.isFinite(rule.value));
  const moveRule = (index: number, direction: number) => {
    const rules = [...draft.rules];
    [rules[index], rules[index + direction]] = [rules[index + direction], rules[index]];
    setDraft({ ...draft, rules });
  };
  const colorOptions = colors.map((color) => ({ value: color, label: t(`numberChart.colors.${color}`) }));
  return <Drawer open width={440} title={t(seriesOnly ? 'numberChart.seriesSettings' : 'charts.displaySettings')} onOpenChange={(open) => { if (!open) onClose(); }} footer={<>
    <Button icon={<RotateCcw className="size-3.5" />} onClick={() => { setDraft(structuredClone(DEFAULT_NUMBER_SETTINGS)); setFormat(DEFAULT_NUMBER_SETTINGS.format); setCustom(false); }}>{t('numberChart.resetDefaults')}</Button>
    <Button variant="primary" disabled={invalid} icon={<Check className="size-3.5" />} onClick={() => { onApply(draft, custom ? format : undefined); onClose(); }}>{t('numberChart.applySettings')}</Button>
  </>}>
    <div className="space-y-5 font-mono text-sm">
      {seriesOnly ? <><div className="flex gap-2" role="group" aria-label={t('numberChart.formatMode')}><Button aria-pressed={!custom} variant={!custom ? 'secondary' : 'ghost'} onClick={() => setCustom(false)}>{t('numberChart.inherit')}</Button><Button aria-pressed={custom} variant={custom ? 'secondary' : 'ghost'} onClick={() => setCustom(true)}>{t('numberChart.custom')}</Button></div>{custom ? <FormatFields value={format} onChange={setFormat} /> : <p className="text-xs text-muted">{t('numberChart.inheritHint')}</p>}</> : <>
        <section className="space-y-4 border-b border-border pb-5"><h3 className="font-semibold">{t('numberChart.outputFormat')}</h3><FormatFields value={draft.format} onChange={(format) => setDraft({ ...draft, format })} /></section>
        <label className="block space-y-2"><span>{t('numberChart.color')}</span><Select value={draft.color} aria-label={t('numberChart.color')} options={colorOptions} onValueChange={(color) => setDraft({ ...draft, color: color as NumberColor })} /></label>
        <section className="space-y-3 border-b border-border pb-5"><p>{t('numberChart.conditionalColors')}</p><p className="text-xs text-muted">{t('numberChart.rulesHint')}</p>
          {draft.rules.map((rule, index) => <div key={rule.id} className="space-y-2 rounded border border-border p-2">
            <div className="flex flex-wrap items-center gap-2"><span className="mr-auto text-xs text-muted">{index + 1}</span><Button variant="link" size="sm" disabled={index === 0} icon={<ArrowUp className="size-3.5" />} onClick={() => moveRule(index, -1)}>{t('numberChart.moveUp')}</Button><Button variant="link" size="sm" disabled={index === draft.rules.length - 1} icon={<ArrowDown className="size-3.5" />} onClick={() => moveRule(index, 1)}>{t('numberChart.moveDown')}</Button></div>
            <div className="flex gap-2"><Select value={rule.operator} aria-label={t('numberChart.operator', { index: index + 1 })} options={(['gt', 'gte', 'lt', 'lte', 'eq'] as const).map((operator) => ({ value: operator, label: ({ gt: '>', gte: '≥', lt: '<', lte: '≤', eq: '=' })[operator] }))} onValueChange={(operator) => setDraft({ ...draft, rules: draft.rules.map((item) => item.id === rule.id ? { ...item, operator: operator as typeof rule.operator } : item) })} /><Input type="number" value={Number.isFinite(rule.value) ? String(rule.value) : ''} aria-label={t('numberChart.threshold', { index: index + 1 })} invalid={!Number.isFinite(rule.value)} onChange={(event) => setDraft({ ...draft, rules: draft.rules.map((item) => item.id === rule.id ? { ...item, value: event.target.value === '' ? NaN : Number(event.target.value) } : item) })} /></div>
            <div className="flex gap-2"><Select value={rule.color} aria-label={t('numberChart.ruleColor', { index: index + 1 })} options={colorOptions} onValueChange={(color) => setDraft({ ...draft, rules: draft.rules.map((item) => item.id === rule.id ? { ...item, color: color as NumberColor } : item) })} /><Button variant="link" danger icon={<Trash2 className="size-3.5" />} onClick={() => setDraft({ ...draft, rules: draft.rules.filter((item) => item.id !== rule.id) })}>{t('numberChart.removeRule')}</Button></div>
          </div>)}
          <Button variant="link" disabled={draft.rules.length >= 12} icon={<Plus className="size-3.5" />} onClick={() => setDraft({ ...draft, rules: [...draft.rules, { id: crypto.randomUUID(), operator: 'gte', value: 0, color: 'success' }] })}>{t('numberChart.addRule')}</Button>
        </section>
        <label className="block space-y-2 border-b border-border pb-5"><span>{t('numberChart.background')}</span><Select value={draft.background} aria-label={t('numberChart.background')} options={(draft.background === 'bar' ? ['none', 'line', 'area', 'bar'] as const : ['none', 'line', 'area'] as const).map((background) => ({ value: background, label: t(`numberChart.backgrounds.${background}`) }))} onValueChange={(background) => setDraft({ ...draft, background: background as NumberDisplaySettings['background'] })} /><p className="text-xs text-muted">{t('numberChart.backgroundHint')}</p></label>
        {draft.background !== 'none' && <label className="block space-y-2"><span>{t('numberChart.backgroundColor')}</span><Select value={draft.backgroundColor ?? 'inherit'} aria-label={t('numberChart.backgroundColor')} options={[{ value: 'inherit', label: t('numberChart.inheritColor') }, ...colorOptions]} onValueChange={(color) => setDraft({ ...draft, backgroundColor: color === 'inherit' ? undefined : color as NumberColor })} /></label>}
        <p className="text-xs text-muted">{t('numberChart.overrideHint')}</p>
      </>}
    </div>
  </Drawer>;
}
