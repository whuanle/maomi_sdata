import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Select, type TimeRange } from '@/design-system';
import { buildGroupKeysSql, useFacetEntries } from '@/pages/search/facets';
import { SEARCH_FIELDS } from '@/api/search';
import type { ChartSource } from '@/api/chartTypes';

/** 提示按需加载，只查询当前时间窗的有界样本，不执行正在编辑的 SQL. */
export function DesignerFieldHints({ range, source: controlledSource }: { range: TimeRange; source?: ChartSource }) {
  const { t } = useTranslation();
  const [localSource, setSource] = useState<ChartSource>('logs');
  const source = controlledSource ?? localSource;
  const [root, setRoot] = useState('attributes');
  const [enabled, setEnabled] = useState(false);
  const roots = SEARCH_FIELDS[source].filter(field => field.kind === 'json').map(field => field.field);
  const selected = roots.includes(root) ? root : roots[0];
  const facets = useFacetEntries({ domain: source, from: range.from, to: range.to, denoise: false,
    enabled: enabled && Boolean(selected), resetKey: source + selected,
    buildSql: () => selected ? buildGroupKeysSql(source, false, selected, 50) : '' });
  return <details className="rounded border border-border p-3">
    <summary className="cursor-pointer text-sm">{t('designer.fieldHints')}</summary>
    <div className="mt-3 flex flex-wrap items-center gap-2">
      <Select className="w-40 shrink-0" disabled={Boolean(controlledSource)} aria-label={t('designer.source')} value={source} options={Object.keys(SEARCH_FIELDS).map(value => ({ value, label: value }))}
        onValueChange={value => { setSource(value as ChartSource); setEnabled(false); }} />
      {selected && <Select className="w-40 shrink-0" aria-label={t('designer.jsonRoot')} value={selected} options={roots.map(value => ({ value, label: value }))}
        onValueChange={value => { setRoot(value); setEnabled(false); }} />}
      {selected && <Button className="shrink-0" loading={facets.loading} onClick={() => { if (enabled) facets.reload(); else setEnabled(true); }}>{t('designer.loadHints')}</Button>}
    </div>
    <p className="mt-2 break-words font-mono text-xs text-muted">{SEARCH_FIELDS[source].map(field => field.field).join(' · ')}</p>
    {facets.error && <p role="alert" className="mt-2 text-xs text-error">{facets.error}</p>}
    {enabled && <><p className="mt-2 text-xs text-muted">{t('designer.sampleHints')}</p>
      <div className="mt-2 flex flex-wrap gap-2">{facets.entries.map(entry => <code key={entry.name} className="break-all rounded bg-surface-2 p-1 text-xs">{selected}[{JSON.stringify(entry.name)}]</code>)}</div></>}
  </details>;
}
