import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Input, Select, Switch } from '@/design-system';
import type { TableDisplaySettings } from '@/api/chartTypes';

export function TableSettings({ value, columns, onChange }: {
  value: TableDisplaySettings; columns: string[]; onChange: (value: TableDisplaySettings) => void;
}) {
  const { t } = useTranslation();
  const [column, setColumn] = useState('');
  const selected = columns.includes(column) ? column : '';
  const settings = selected ? { ...value, ...value.columns?.[selected] } : value;
  const update = (patch: Partial<TableDisplaySettings>) => onChange(selected
    ? { ...value, columns: { ...value.columns, [selected]: { ...value.columns?.[selected], ...patch } } }
    : { ...value, ...patch });
  return <div className="w-64 space-y-3">
    <label className="flex items-center justify-between">{t('charts.tableStriped')}<Switch aria-label={t('charts.tableStriped')} checked={value.striped ?? false} onChange={(striped) => onChange({ ...value, striped })} /></label>
    <label className="flex items-center justify-between">{t('charts.tableSticky')}<Switch aria-label={t('charts.tableSticky')} checked={value.stickyHeader !== false} onChange={(stickyHeader) => onChange({ ...value, stickyHeader })} /></label>
    <label className="block">{t('charts.tableColumn')}<Select value={selected || '*'} onValueChange={(next) => setColumn(next === '*' ? '' : next)} options={[{ value: '*', label: t('charts.tableAllColumns') }, ...columns.map((name) => ({ value: name, label: name }))]} /></label>
    <label className="block">{t('charts.tableFormat')}<Select value={settings.format ?? 'number'} onValueChange={(format) => update({ format: format as TableDisplaySettings['format'] })} options={[{ value: 'number', label: t('charts.tableNumber') }, { value: 'percent', label: '%' }, { value: 'bytes', label: t('charts.tableBytes') }]} /></label>
    <label className="block">{t('charts.tableDecimals')}<Input type="number" min={0} max={8} value={settings.decimals ?? 2} onChange={(event) => update({ decimals: Math.max(0, Math.min(8, Number(event.target.value))) })} /></label>
    <label className="block">{t('charts.tableColor')}<Select value={settings.color ?? 'default'} onValueChange={(color) => update({ color: color as TableDisplaySettings['color'] })} options={['default', 'primary', 'success', 'warning'].map((color) => ({ value: color, label: t(`charts.tableColor_${color}`) }))} /></label>
    <label className="flex items-center justify-between">{t('charts.tableRowClick')}<Switch aria-label={t('charts.tableRowClick')} checked={value.rowClick !== false} onChange={(rowClick) => onChange({ ...value, rowClick })} /></label>
  </div>;
}
