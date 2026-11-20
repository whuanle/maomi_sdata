import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { RotateCcw, Check } from 'lucide-react';
import { Button, Input, Modal, Select, Switch } from '@/design-system';
import type { BarDisplaySettings, ChartOutputFormat, ChartValueFormat } from '@/api/chartTypes';
import { DEFAULT_VALUE_FORMAT, formatBarValue } from './barFormat';

export function ValueFormatFields({ value, onChange }: { value: ChartValueFormat; onChange: (value: ChartValueFormat) => void }) {
  const { t } = useTranslation();
  return <div className="space-y-4">
    <label className="block space-y-2"><span>{t('charts.outputFormat')}</span>
      <Select value={value.output} aria-label={t('charts.outputFormat')} onValueChange={(output) => onChange({ ...value, output: output as ChartOutputFormat })} options={(['number', 'currency', 'percentage', 'duration', 'time', 'data', 'data_rate', 'throughput'] as const).map((output) => ({ value: output, label: t(`charts.format_${output}`) }))} />
    </label>
    <div className="rounded border border-border bg-surface-2 p-3"><span className="text-muted">{t('charts.formatExample')}</span><div className="mt-1 font-mono">{formatBarValue(1234, value)}</div></div>
    <label className="block space-y-2"><span>{t('charts.decimals')}: {value.decimals}</span>
      <input className="w-full accent-primary" type="range" min={0} max={10} value={value.decimals} aria-label={t('charts.decimals')} onChange={(event) => onChange({ ...value, decimals: Number(event.target.value) })} />
      <span className="flex justify-between text-xs text-muted"><span>0</span><span>10</span></span>
    </label>
    <label className="flex items-center gap-2"><Switch checked={value.thousands} onChange={(thousands) => onChange({ ...value, thousands })} aria-label={t('charts.separateThousands')} />{t('charts.separateThousands')}</label>
    <label className="flex items-center gap-2"><Switch checked={value.compact} onChange={(compact) => onChange({ ...value, compact })} aria-label={t('charts.largeNumberFormat')} />{t('charts.largeNumberFormat')}</label>
    <p className="text-xs text-muted">{t('charts.formatUnitsHint')}</p>
  </div>;
}

/** 浮层使用独立草稿，只有 Apply 才提交. */
export function BarDisplayModal({ settings, onApply, onClose, seriesOnly = false }: { settings: BarDisplaySettings; onApply: (settings: BarDisplaySettings) => void; onClose: () => void; seriesOnly?: boolean }) {
  const { t } = useTranslation();
  const [format, setFormat] = useState(settings.format);
  const [limit, setLimit] = useState(settings.seriesLimit == null ? '' : String(settings.seriesLimit));
  const invalid = limit !== '' && (!/^\d+$/.test(limit) || Number(limit) < 1 || Number(limit) > 10000);
  return <Modal open onOpenChange={(open) => { if (!open) onClose(); }} title={t(seriesOnly ? 'charts.seriesFormat' : 'charts.displaySettings')} footer={<>
    <Button icon={<RotateCcw className="size-3.5" />} onClick={() => { setFormat({ ...DEFAULT_VALUE_FORMAT }); setLimit(''); }}>{t('charts.resetDefaults')}</Button>
    <Button variant="primary" disabled={invalid && !seriesOnly} icon={<Check className="size-3.5" />} onClick={() => { onApply({ format, seriesLimit: limit === '' ? undefined : Number(limit) }); onClose(); }}>{t('charts.applySettings')}</Button>
  </>}>
    <div className="space-y-5">
      {!seriesOnly && <label className="block space-y-2 border-b border-border pb-5"><span>{t('charts.seriesLimit')}</span><p className="text-xs text-muted">{t('charts.seriesLimitHint')}</p><Input value={limit} aria-label={t('charts.seriesLimit')} placeholder={t('charts.seriesLimitPlaceholder')} invalid={invalid} onChange={(event) => setLimit(event.target.value)} />{invalid && <p role="alert" className="text-xs text-error">{t('charts.invalidSeriesLimit')}</p>}</label>}
      <ValueFormatFields value={format} onChange={setFormat} />
      {!seriesOnly && <p className="rounded border border-border p-3 text-xs text-muted">{t('charts.seriesFormatOverride')}</p>}
    </div>
  </Modal>;
}
