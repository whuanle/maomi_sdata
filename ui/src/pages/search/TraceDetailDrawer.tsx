import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Copy } from 'lucide-react';
import { Button, Drawer, Empty, SpinArea, Tabs, TabsList, TabsTrigger, TabsContent, toast } from '@/design-system';
import { loadTraceLogs, loadTraceWindow, type TraceRow } from '@/api/traceSearch';
import type { SearchDomain, SpanItem } from '@/api/types';
import { formatDateTime, formatDurationUs } from '@/utils/format';
import { SpanPreview } from './SpanPreview';
import { TraceWaterfall } from './TraceWaterfall';
import { ServiceMapTab } from './ServiceMapTab';
import { LogDetailDrawer } from './LogDetailDrawer';
import { ErrorHint, MiniLogList } from './detailShared';
import { buildTraceTree, spanEvents, timestampUs } from './traceTimeline';
import { SurroundingContextTab } from './SurroundingContextTab';
import { JsonTree } from './JsonTree';

export function TraceDetailDrawer({ row, from, to, onClose, onFilterByValue, onFilterOnlyValue }: {
  row: TraceRow | null; from: string; to: string; onClose: () => void;
  onFilterByValue: (field: string, value: unknown, domain?: SearchDomain) => void;
  onFilterOnlyValue: (field: string, value: unknown, domain?: SearchDomain) => void;
}) {
  const { t } = useTranslation();
  return <Drawer open={row !== null} onOpenChange={(open) => { if (!open) onClose(); }}
    width="96vw" title={String(row?.name ?? row?.trace_id ?? t('traces.waterfall'))}>
    {row && <TraceDetail key={String(row.trace_id) + from + to} row={row} from={from} to={to}
      onFilterByValue={onFilterByValue} onFilterOnlyValue={onFilterOnlyValue} />}
  </Drawer>;
}

function TraceDetail({ row, from, to, onFilterByValue, onFilterOnlyValue }: {
  row: TraceRow; from: string; to: string;
  onFilterByValue: (field: string, value: unknown, domain?: SearchDomain) => void;
  onFilterOnlyValue: (field: string, value: unknown, domain?: SearchDomain) => void;
}) {
  const { t } = useTranslation();
  const traceId = String(row.trace_id ?? '');
  const [spans, setSpans] = useState<TraceRow[]>([]);
  const [logs, setLogs] = useState<TraceRow[]>([]);
  const [selected, setSelected] = useState(row);
  const [log, setLog] = useState<TraceRow | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [logError, setLogError] = useState('');
  const [limited, setLimited] = useState(false);
  const [logsLimited, setLogsLimited] = useState(false);
  const [retry, setRetry] = useState(0);
  const [tab, setTab] = useState('trace');
  const [contextAnchor, setContextAnchor] = useState(row);
  const [contextSpan, setContextSpan] = useState<TraceRow | null>(null);
  const [eventDetail, setEventDetail] = useState<ReturnType<typeof spanEvents>[number] | null>(null);
  const contextRoot = useRef<HTMLDivElement>(null);
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    setLogError('');
    setSpans([]);
    setLogs([]);
    loadTraceWindow(traceId, from, to).then((response) => {
      if (cancelled) return;
      setSpans(response.rows);
      setLimited(response.truncated);
      setSelected(response.rows.find((span) => span.span_id === row.span_id) ?? response.rows[0] ?? row);
    }).catch((err: unknown) => {
      if (!cancelled) setError(err instanceof Error ? err.message : String(err));
    }).finally(() => { if (!cancelled) setLoading(false); });
    loadTraceLogs(traceId, from, to).then((response) => {
      if (!cancelled) { setLogs(response.rows); setLogsLimited(response.truncated); }
    }).catch((err: unknown) => {
      if (!cancelled) setLogError(err instanceof Error ? err.message : String(err));
    });
    return () => { cancelled = true; };
  }, [traceId, from, to, retry, row]);
  const items = useMemo<SpanItem[]>(() => spans.map((span) => ({
    traceId: String(span.trace_id), spanId: String(span.span_id), parentSpanId: span.parent_span_id == null ? null : String(span.parent_span_id),
    timestamp: String(span.ts), durationUs: Number(span.duration_us), service: String(span.service),
    name: String(span.name), kind: String(span.kind), status: String(span.status),
    attributes: span.attributes as string | null, resource: span.resource as string | null, events: span.events as string | null,
  })), [spans]);
  const timedSpans = spans.filter((span) => Number.isFinite(timestampUs(span.ts)) && Number.isFinite(Number(span.duration_us)));
  const duration = timedSpans.length ? Math.max(...timedSpans.map((span) => timestampUs(span.ts) + Math.max(0, Number(span.duration_us)))) - Math.min(...timedSpans.map((span) => timestampUs(span.ts))) : 0;
  const incomplete = useMemo(() => buildTraceTree(spans).incomplete, [spans]);
  return <div className="flex min-w-0 flex-col gap-4">
    <div className="flex flex-wrap items-center gap-3 font-mono text-xs text-muted">
      <strong className="text-fg">{String(selected.name ?? '')}</strong>
      <span>{selected.ts ? formatDateTime(String(selected.ts)) : ''}</span>
      <span>{String(selected.service ?? '')}</span>
      <span>{formatDurationUs(Number(selected.duration_us ?? 0))}</span>
      <span>{String(selected.status ?? '')} · {String(selected.kind ?? '')}</span>
      <Button variant="link" size="sm" icon={<Copy />} onClick={() => {
        void navigator.clipboard.writeText(traceId).then(() => toast.success(t('charts.copied'))).catch(() => toast.error(t('search.copyFailed')));
      }}>Trace ID: {traceId}</Button>
    </div>
    <p className="text-xs text-subtle">{t('search.traceWindow', { from: formatDateTime(from), to: formatDateTime(to), duration: formatDurationUs(duration) })}</p>
    {(limited || incomplete) && <p role="status" className="text-xs text-warning">{t('search.traceIncomplete')}</p>}
    {loading && <SpinArea />}
    {error && <ErrorHint message={error} onRetry={() => setRetry((value) => value + 1)} />}
    {!loading && !error && spans.length === 0 && <Empty description={t('search.traceEmpty')} />}
    <Tabs value={tab} onValueChange={(value) => { if (value === 'context' && tab !== 'context') { setContextAnchor(selected); setContextSpan(null); } setTab(value); }}>
      <TabsList>
        <TabsTrigger value="trace">{t('search.traceTab')}</TabsTrigger>
        <TabsTrigger value="logs">{t('search.drawerTabTraceLogs')}</TabsTrigger>
        <TabsTrigger value="map">{t('search.drawerTabServiceMap')}</TabsTrigger>
        <TabsTrigger value="context">{t('search.drawerTabContext')}</TabsTrigger>
      </TabsList>
      <TabsContent value="trace">
        <div className="grid min-w-0 grid-cols-1 gap-4 pt-3 xl:grid-cols-[minmax(0,1.8fr)_minmax(320px,1fr)]">
          <div className="min-w-0">
            {logError && <ErrorHint message={logError} onRetry={() => setRetry((value) => value + 1)} />}
            {logsLimited && <p role="status" className="text-xs text-warning">{t('search.traceIncomplete')}</p>}
            <TraceWaterfall spans={spans} logs={logs} selectedId={String(selected.span_id ?? '')} onSelect={(span) => { setSelected(span); setEventDetail(null); }} onLogSelect={setLog} onEventSelect={setEventDetail} />
            {eventDetail && <section className="border-t border-border p-3"><h3 className="text-xs text-muted">{t('search.traceEventDetail')}</h3><JsonTree value={eventDetail} /></section>}
          </div>
          <div className="min-w-0 border-t border-border pt-3 xl:border-t-0 xl:border-l xl:pt-0 xl:pl-4">
            <SpanPreview key={String(selected.span_id)} row={selected} onFilterByValue={onFilterByValue} onFilterOnlyValue={onFilterOnlyValue} />
          </div>
        </div>
      </TabsContent>
      <TabsContent value="logs" className="pt-3">
        {logError && <ErrorHint message={logError} onRetry={() => setRetry((value) => value + 1)} />}
        {logsLimited && <p className="text-xs text-warning">{t('search.traceIncomplete')}</p>}
        {!logError && !logs.length && <Empty description={t('search.traceEmpty')} />}
        <MiniLogList rows={logs} currentTs="" onSelect={setLog} showHeader />
      </TabsContent>
      <TabsContent value="map" className="pt-3"><ServiceMapTab traceId={traceId} items={items} /></TabsContent>
      <TabsContent value="context" className="pt-3">
        <div ref={contextRoot} className="max-h-[65vh] overflow-auto">
          <SurroundingContextTab record={contextAnchor} currentTs={String(contextAnchor.ts ?? '')} domain="traces" allowDomainSwitch scrollRoot={contextRoot}
            onSelect={(item, domain) => { if (domain === 'logs') setLog(item); else setContextSpan(item); }} />
        </div>
        {contextSpan && <section className="mt-3 border-t border-border pt-3"><SpanPreview row={contextSpan} onFilterByValue={onFilterByValue} onFilterOnlyValue={onFilterOnlyValue} /></section>}
      </TabsContent>
    </Tabs>
    <LogDetailDrawer domain="logs" row={log} onClose={() => setLog(null)}
      onFilterByValue={(field, value) => onFilterByValue(field, value, 'logs')} onFilterOnlyValue={(field, value) => onFilterOnlyValue(field, value, 'logs')} onTraceActivate={() => setLog(null)} />
  </div>;
}
