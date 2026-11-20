import { useEffect, useMemo, useState } from 'react';
import { Check, ChevronDown, ChevronRight, List, Search, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, Empty, Input, Modal, Popover, Tag, type TimeRange } from '@/design-system';
import { queryChartMetrics, queryChartMetricDetails, type ChartMetric } from '@/api/chartMetrics';
import { cn } from '@/utils/cn';

function MetricDetails({ metric, range, onFilter }: { metric: ChartMetric; range: TimeRange; onFilter?: (field: string, value: string) => void }) {
  const { t } = useTranslation();
  const [labels, setLabels] = useState<Record<string, unknown>[]>([]);
  const [search, setSearch] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [truncated, setTruncated] = useState(false);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    let cancelled = false;
    setLoading(true); setError(''); setLabels([]); setExpanded(null);
    queryChartMetricDetails(metric.name, range).then((result) => {
      if (!cancelled) { setLabels(result.labels); setTruncated(result.truncated); }
    }).catch((err: unknown) => { if (!cancelled) setError(String(err instanceof Error ? err.message : err)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [metric.name, range.from, range.to, nonce]);
  const attributes = useMemo(() => {
    const values = new Map<string, Set<string>>();
    const visit = (value: Record<string, unknown>, path: string[] = []) => {
      for (const [key, item] of Object.entries(value)) {
        const next = [...path, key];
        if (item && typeof item === 'object' && !Array.isArray(item)) visit(item as Record<string, unknown>, next);
        else {
          const field = 'labels' + next.map((part) => '[' + JSON.stringify(part) + ']').join('');
          if (!values.has(field)) values.set(field, new Set());
          values.get(field)!.add(typeof item === 'string' ? item : JSON.stringify(item) ?? '');
        }
      }
    };
    labels.forEach((value) => visit(value));
    return [...values].sort(([a], [b]) => a.localeCompare(b));
  }, [labels]);
  return <div className="min-w-0 flex-1 space-y-3" data-testid="metric-details">
    <div className="flex flex-wrap items-center gap-2"><strong className="break-all font-mono">{metric.name}</strong>{metric.kinds.map((kind) => <Tag key={kind}>{kind}</Tag>)}</div>
    <p className="text-xs text-muted">{t('metricPicker.metadataHint')}</p>
    <h3 className="text-xs text-muted">{t('metricPicker.attributes')} <Tag>{attributes.length}</Tag></h3>
    <Input value={search} onChange={(event) => setSearch(event.target.value)} placeholder={t('metricPicker.searchAttributes')} aria-label={t('metricPicker.searchAttributes')} />
    {loading && <p role="status">{t('common.loading')}</p>}
    {error && <div role="alert" className="text-error">{error}<Button variant="link" size="sm" icon={<Search className="size-3.5" />} onClick={() => setNonce((value) => value + 1)}>{t('metricPicker.retry')}</Button></div>}
    {!loading && !error && attributes.length === 0 && <Empty description={t('metricPicker.noAttributes')} />}
    <div className="flex flex-wrap gap-2">{attributes.filter(([field]) => field.toLowerCase().includes(search.toLowerCase())).map(([field]) =>
      <Button key={field} size="sm" variant="secondary" icon={<ChevronRight className="size-3.5" />} aria-expanded={expanded === field} onClick={() => setExpanded(expanded === field ? null : field)}>{field}</Button>)}</div>
    {expanded && <div className="space-y-1 rounded border border-border p-2">{[...(attributes.find(([field]) => field === expanded)?.[1] ?? [])].map((value) =>
      <div key={value} className="flex items-center justify-between gap-2"><span className="break-all font-mono text-xs">{value}</span>{onFilter && <Button variant="link" size="sm" icon={<Check className="size-3.5" />} onClick={() => onFilter(expanded, value)}>{t('metricPicker.addFilter')}</Button>}</div>)}</div>}
    {truncated && <p role="status" className="text-xs text-warning">{t('metricPicker.sampleHint')}</p>}
  </div>;
}

/** 下拉立即选名，浏览器维护独立草稿，确认后才回填所属序列. */
export function MetricPicker({ value, onChange, range, index, onFilter }: { value: string; onChange: (name: string) => void; range: TimeRange; index: number; onFilter: (field: string, value: string) => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [browse, setBrowse] = useState(false);
  const [details, setDetails] = useState(false);
  const [search, setSearch] = useState('');
  const [draft, setDraft] = useState(value);
  const [tree, setTree] = useState(true);
  const [groups, setGroups] = useState<string[]>([]);
  const [metrics, setMetrics] = useState<ChartMetric[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [truncated, setTruncated] = useState(false);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    if (!open && !browse && !details) return;
    let cancelled = false;
    setLoading(true); setError(''); setMetrics([]);
    queryChartMetrics(range).then((result) => { if (!cancelled) { setMetrics(result.items); setTruncated(result.truncated); } })
      .catch((err: unknown) => { if (!cancelled) setError(err instanceof Error ? err.message : String(err)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [open, browse, details, range.from, range.to, nonce]);
  const filtered = metrics.filter((metric) => metric.name.toLowerCase().includes(search.toLowerCase()));
  const selected = metrics.find((metric) => metric.name === (browse ? draft : value));
  const groupName = (name: string) => name.split(/[._]/)[0];
  const grouped = [...new Set(filtered.map((metric) => groupName(metric.name)))];
  const status = <>{loading && <p role="status" className="p-2 text-muted">{t('common.loading')}</p>}{error && <div role="alert" className="p-2 text-error">{error}<Button variant="link" size="sm" icon={<Search className="size-3.5" />} onClick={() => setNonce((n) => n + 1)}>{t('metricPicker.retry')}</Button></div>}{!loading && !error && !filtered.length && <Empty description={t('metricPicker.empty')} />}{truncated && <p role="status" className="text-xs text-warning">{t('metricPicker.catalogTruncated')}</p>}</>;
  const pick = (name: string) => { if (browse) setDraft(name); else { onChange(name); setOpen(false); } };
  const row = (metric: ChartMetric) => <button type="button" key={metric.name} role="option" aria-selected={(browse ? draft : value) === metric.name} onClick={() => pick(metric.name)} className={cn('flex w-full items-center justify-between gap-2 rounded px-2 py-2 text-left text-xs hover:bg-surface-2', (browse ? draft : value) === metric.name && 'bg-primary-soft')}>
    <span className="min-w-0 break-all font-mono">{metric.name}</span><span className="shrink-0 text-muted">{metric.kinds.join(', ') || '—'}</span>
  </button>;
  return <div className="min-w-0 space-y-2">
    <div className="flex flex-wrap items-center gap-2">
      <Popover.Root open={open} onOpenChange={(next) => { setOpen(next); setSearch(''); }}>
        <Popover.Trigger asChild><Button variant="secondary" size="sm" icon={<ChevronDown className="size-3.5" />} role="combobox" aria-expanded={open} aria-label={t('metricPicker.select', { index: index + 1 })} testId={'metric-picker-' + index}>{value || t('metricPicker.search')}</Button></Popover.Trigger>
        <Popover.Content align="start" className="w-96 max-w-[85vw] p-2">
          <Input value={search} onChange={(event) => setSearch(event.target.value)} placeholder={t('metricPicker.search')} aria-label={t('metricPicker.search')} />
          <div role="listbox" aria-label={t('metricPicker.metrics')} className="max-h-72 overflow-auto">{status}{filtered.map(row)}</div>
        </Popover.Content>
      </Popover.Root>
      {value && <Button variant="link" size="sm" icon={<X className="size-3.5" />} onClick={() => onChange('')}>{t('metricPicker.clear')}</Button>}
      <Button variant="link" size="sm" icon={<Search className="size-3.5" />} onClick={() => { setBrowse(true); setDraft(value); setSearch(''); }}>{t('metricPicker.browse')}</Button>
    </div>
    {value && <div className="rounded border border-border px-2 py-1"><Button variant="link" size="sm" icon={<ChevronDown className="size-3.5" />} aria-expanded={details} onClick={() => setDetails(!details)}>{value}</Button>{details && (selected ? <MetricDetails metric={selected} range={range} onFilter={onFilter} /> : status)}</div>}
    {browse && <Modal open onOpenChange={setBrowse} title={t('metricPicker.browse')} width="1200px" footer={<><Button onClick={() => setBrowse(false)}>{t('common.cancel')}</Button><Button variant="primary" disabled={!draft || !metrics.some((metric) => metric.name === draft)} icon={<Check className="size-3.5" />} onClick={() => { onChange(draft); setBrowse(false); }}>{t('metricPicker.use')}</Button></>}>
      <div className="flex min-h-96 flex-col gap-4 md:flex-row">
        <div className="min-w-0 space-y-2 md:w-2/5 md:border-r md:border-border md:pr-4">
          <div className="flex flex-wrap gap-2"><Input value={search} onChange={(event) => setSearch(event.target.value)} placeholder={t('metricPicker.search')} aria-label={t('metricPicker.search')} /><Button size="sm" variant={tree ? 'secondary' : 'ghost'} aria-pressed={tree} icon={<ChevronRight className="size-3.5" />} onClick={() => setTree(true)}>{t('metricPicker.tree')}</Button><Button size="sm" variant={!tree ? 'secondary' : 'ghost'} aria-pressed={!tree} icon={<List className="size-3.5" />} onClick={() => setTree(false)}>{t('metricPicker.list')}</Button></div>
          <div className="flex justify-between border-b border-border pb-2 text-xs text-muted"><span>{t('metricPicker.metric')}</span><span>{t('metricPicker.type')}</span></div>
          <div role="listbox" aria-label={t('metricPicker.metrics')} className="max-h-96 overflow-auto">{status}{tree ? grouped.map((group) => <div key={group}><Button variant="link" size="sm" icon={groups.includes(group) ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />} aria-expanded={groups.includes(group) || !!search} onClick={() => setGroups((items) => items.includes(group) ? items.filter((item) => item !== group) : [...items, group])}>{group} ({filtered.filter((metric) => groupName(metric.name) === group).length})</Button>{(groups.includes(group) || !!search) && <div className="pl-4">{filtered.filter((metric) => groupName(metric.name) === group).map(row)}</div>}</div>) : filtered.map(row)}</div>
        </div>
        {selected ? <MetricDetails key={selected.name} metric={selected} range={range} /> : <div className="flex flex-1 items-center justify-center text-muted">{t('metricPicker.choose')}</div>}
      </div>
    </Modal>}
  </div>;
}
