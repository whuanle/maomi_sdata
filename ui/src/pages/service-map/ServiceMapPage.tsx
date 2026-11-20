import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { Play, RotateCcw, Search } from 'lucide-react';
import { Button, Chart, Empty, Input, Page, Select, SpinArea, TimeRangePicker } from '@/design-system';
import { loadMapServices, loadServiceMap, loadServiceMapExamples } from '@/api/serviceMap';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { TraceDetailDrawer } from '@/pages/search/TraceDetailDrawer';
import type { TraceRow } from '@/api/traceSearch';
import { formatDateTime, formatDurationUs } from '@/utils/format';
import { useNavigate } from 'react-router';
import { ServiceMapGraph, type MapColor } from './ServiceMapGraph';
import type { MapResult } from './serviceMapModel';
import type { MapQuery } from './serviceMapQuery';
import { parseMapState, resolveRange, serializeMapState, type MapState } from './serviceMapState';

export function ServiceMapPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const initial = useRef(parseMapState(params));
  const [draft, setDraft] = useState<MapState>(initial.current);
  const [execution, setExecution] = useState<MapQuery | null>(null);
  const [result, setResult] = useState<MapResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [services, setServices] = useState<string[]>([]);
  const [servicesLimited, setServicesLimited] = useState(false);
  const [servicesError, setServicesError] = useState('');
  const [serviceRetry, setServiceRetry] = useState(0);
  const [color, setColor] = useState<MapColor>('errorRate');
  const [focus, setFocus] = useState<string | null>(null);
  const [selection, setSelection] = useState<{ source: string; target?: string } | null>(null);
  const [examples, setExamples] = useState<TraceRow[]>([]);
  const [examplesLimited, setExamplesLimited] = useState(false);
  const [examplesLoading, setExamplesLoading] = useState(false);
  const [examplesError, setExamplesError] = useState('');
  const [exampleRetry, setExampleRetry] = useState(0);
  const [trace, setTrace] = useState<TraceRow | null>(null);
  const [reset, setReset] = useState(0);
  const generation = useRef(0);

  async function run(state: MapState) {
    const current = ++generation.current;
    const range = resolveRange(state.range);
    const query = { ...state, from: range.from, to: range.to };
    setDraft({ ...state, range });
    setParams(serializeMapState({ ...state, range }), { replace: true });
    setLoading(true); setError(''); setSelection(null); setTrace(null); setFocus(null); setResult(null);
    setExecution(query);
    try {
      const response = await loadServiceMap(query);
      if (current === generation.current) setResult(response);
    } catch (err) {
      if (current === generation.current) setError(err instanceof Error ? err.message : String(err));
    } finally { if (current === generation.current) setLoading(false); }
  }

  useEffect(() => {
    void run(initial.current);
    return () => { generation.current++; };
    // Only the initial URL executes automatically; edits execute through Run.
  }, []);

  useEffect(() => {
    let cancelled = false;
    setServicesError('');
    loadMapServices(draft.range.from, draft.range.to).then((response) => {
      if (!cancelled) { setServices(response.services); setServicesLimited(response.truncated); }
    }).catch((err: unknown) => { if (!cancelled) setServicesError(err instanceof Error ? err.message : String(err)); });
    return () => { cancelled = true; };
  }, [draft.range.from, draft.range.to, serviceRetry]);

  useEffect(() => {
    let cancelled = false;
    setExamples([]); setExamplesError(''); setExamplesLimited(false);
    if (!selection || !execution) { setExamplesLoading(false); return; }
    setExamplesLoading(true);
    loadServiceMapExamples(execution, selection).then((response) => {
      if (!cancelled) { setExamples(response.rows); setExamplesLimited(response.truncated); }
    }).catch((err: unknown) => { if (!cancelled) setExamplesError(err instanceof Error ? err.message : String(err)); })
      .finally(() => { if (!cancelled) setExamplesLoading(false); });
    return () => { cancelled = true; };
  }, [execution, selection, exampleRetry]);

  function searchValue(field: string, value: unknown) {
    if (!execution) return;
    const target = new URLSearchParams({ source: 'traces', filters: JSON.stringify([{ id: 1, field, op: 'eq', value: String(value ?? '') }]), from: execution.from, to: execution.to });
    navigate('/search?' + target.toString());
  }

  const metric = selection?.target !== undefined
    ? result?.edges.find((edge) => edge.source === selection.source && edge.target === selection.target)
    : result?.nodes.find((node) => node.source === selection?.source);
  const servicesOptions = [...new Set([draft.service, ...services])].filter(Boolean);
  return <Page>
    <div className="flex h-full min-h-0 min-w-0 flex-col gap-3 [&>*]:shrink-0" data-testid="map-workspace">
      <div className="flex min-w-0 flex-col gap-2 border-b border-border pb-3 xl:flex-row xl:items-center" data-testid="map-toolbar">
        <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2 sm:flex-nowrap" data-testid="map-query-controls">
          <Select className="w-48 max-w-full shrink-0" testId="map-service" value={draft.service ? 'service:' + draft.service : '__all__'} onValueChange={(value) => setDraft({ ...draft, service: value === '__all__' ? '' : value.slice('service:'.length) })}
            options={[{ value: '__all__', label: t('serviceMap.allServices') }, ...servicesOptions.map((service) => ({ value: 'service:' + service, label: service }))]} />
          <Select className="w-24 shrink-0" testId="map-syntax" value={draft.syntax} onValueChange={(value) => setDraft({ ...draft, syntax: value as MapState['syntax'] })}
            options={[{ value: 'lucene', label: 'Lucene' }, { value: 'sql', label: 'SQL' }]} />
          <WhereSuggestInput domain="traces" syntax={draft.syntax} value={draft.query} onChange={(query) => setDraft({ ...draft, query })}
            onRun={() => void run(draft)} testId="map-query" ariaLabel={t('serviceMap.filter')} placeholder={t('serviceMap.filter')}
            from={draft.range.from} to={draft.range.to} denoise={false} className="min-w-0 flex-[1_1_100%] sm:flex-[1_1_12rem]" />
        </div>
        <div className="flex max-w-full shrink-0 flex-wrap items-center gap-2" data-testid="map-execution-controls">
          <label className="flex shrink-0 items-center gap-2 whitespace-nowrap text-xs text-muted">{t('serviceMap.sampling')}
            <Input type="number" min={1} max={100} aria-label={t('serviceMap.sampling')} value={draft.sampling}
              onChange={(event) => setDraft({ ...draft, sampling: Number(event.target.value) })} className="w-16 shrink-0" />%
          </label>
          <TimeRangePicker value={draft.range} onChange={(range) => setDraft({ ...draft, range })} allowLive={false} className="max-w-full shrink-0" />
          <Button icon={<Play />} onClick={() => void run(draft)}>{t('serviceMap.run')}</Button>
        </div>
      </div>
      <p className="text-xs text-muted">{t('serviceMap.queryHint')}</p>
      {servicesLimited && <p className="text-xs text-warning">{t('serviceMap.servicesLimited')}</p>}
      {servicesError && <div role="alert" className="text-xs text-error">{servicesError}
        <Button variant="link" size="sm" icon={<RotateCcw />} onClick={() => setServiceRetry((value) => value + 1)}>{t('serviceMap.retry')}</Button>
      </div>}
      {loading && <SpinArea />}
      {error && <div role="alert" className="text-error">{error}
        <Button variant="link" icon={<RotateCcw />} onClick={() => void run(draft)}>{t('serviceMap.retry')}</Button>
      </div>}
      {result && execution && <>
        <div className="flex flex-wrap items-center gap-4 text-xs text-muted">
          <span className="font-mono">{formatDateTime(execution.from)} → {formatDateTime(execution.to)}</span>
          <span>{t('serviceMap.sampleSummary', { sampled: result.sampledTraces, matched: result.matchedTraces, percent: execution.sampling })}</span>
          <span>{t('serviceMap.rootSummary', { count: result.roots })}</span>
        </div>
        {result.missingParents > 0 && <p role="status" className="text-xs text-warning">{t('serviceMap.missingParents', { count: result.missingParents })}</p>}
        {result.truncated && <p role="status" className="text-xs text-warning">{t('serviceMap.limited', { nodes: result.nodes.length, totalNodes: result.totalNodes, edges: result.edges.length, totalEdges: result.totalEdges })}</p>}
        <p className="text-xs text-subtle">{t('serviceMap.timelineHint')}</p>
        <div className="h-25" data-testid="map-timeline">
        <Chart minHeight={0} option={{
          animation: false, grid: { left: 45, right: 15, top: 8, bottom: 25 },
          tooltip: { trigger: 'axis', renderMode: 'richText' },
          xAxis: { type: 'time', min: Date.parse(execution.from), max: Date.parse(execution.to) }, yAxis: { type: 'value', minInterval: 1 },
          series: [{ type: 'bar', name: t('serviceMap.traces'), data: result.buckets.map((bucket) => [bucket.timestamp, bucket.traces]) }],
        }} onEvents={{ click: (event) => {
          const point = event.data as number[];
          if (!Array.isArray(point)) return;
          const width = Math.max(1000, Math.ceil((Date.parse(execution.to) - Date.parse(execution.from)) / 1000 / 40) * 1000);
          const from = new Date(Math.max(point[0], Date.parse(execution.from))).toISOString();
          const to = new Date(Math.min(point[0] + width, Date.parse(execution.to))).toISOString();
          void run({ ...draft, ...execution, range: { from, to } });
        } }} />
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <Select className="w-48 max-w-full shrink-0" testId="map-color" value={color} onValueChange={(value) => setColor(value as MapColor)} options={(['latency', 'errorRate', 'calls'] as const).map((value) => ({ value, label: t('serviceMap.' + value) }))} />
          <Button variant="link" size="sm" icon={<RotateCcw />} onClick={() => { setFocus(null); setReset((value) => value + 1); }}>{t('serviceMap.reset')}</Button>
          <span className="text-xs text-subtle">{t('serviceMap.legend')}</span>
        </div>
        {!result.nodes.length ? <Empty description={t('serviceMap.empty')} /> : <div className="grid min-h-[360px] min-w-0 flex-1 grid-cols-1 grid-rows-[minmax(360px,1fr)_auto] gap-3 xl:grid-cols-[minmax(0,1fr)_320px] xl:grid-rows-[minmax(0,1fr)]" data-testid="map-canvas-layout">
          <div className="min-h-0 min-w-0 overflow-hidden rounded border border-border bg-surface" data-testid="map-canvas">
            <ServiceMapGraph key={reset} nodes={result.nodes} edges={result.edges} color={color} focus={focus} onSelect={setSelection} />
          </div>
          <section className="max-h-64 min-h-0 min-w-0 overflow-auto rounded border border-border bg-surface p-3 xl:max-h-none" data-testid="map-details">
            {!selection ? <p className="text-xs text-muted">{t('serviceMap.selectHint')}</p> : <>
              <h3 className="break-all text-sm font-semibold">{selection.source || t('serviceMap.unknown')}{selection.target !== undefined && ' → ' + (selection.target || t('serviceMap.unknown'))}</h3>
              {metric && <dl className="my-3 grid grid-cols-2 gap-2 text-xs">
                <dt>{t('serviceMap.calls')}</dt><dd className="font-mono">{metric.calls}</dd>
                <dt>{t('serviceMap.traces')}</dt><dd className="font-mono">{metric.traces}</dd>
                <dt>{t('serviceMap.errorRate')}</dt><dd className="font-mono">{(metric.errors / Math.max(1, metric.calls) * 100).toFixed(1)}%</dd>
                <dt>{t('serviceMap.latency')}</dt><dd className="font-mono">{formatDurationUs(metric.duration)}</dd>
                <dt>{t('serviceMap.firstSeen')}</dt><dd>{formatDateTime(metric.firstSeen)}</dd>
                <dt>{t('serviceMap.lastSeen')}</dt><dd>{formatDateTime(metric.lastSeen)}</dd>
              </dl>}
              {selection.target === undefined && <>
                <Button variant="link" size="sm" icon={<Search />} onClick={() => setFocus(selection.source)}>{t('serviceMap.focus')}</Button>
                <p className="my-2 break-words text-xs text-muted">{t('serviceMap.upstream')}: {result.edges.filter((edge) => edge.target === selection.source).map((edge) => edge.source).join(', ') || '—'}</p>
                <p className="my-2 break-words text-xs text-muted">{t('serviceMap.downstream')}: {result.edges.filter((edge) => edge.source === selection.source).map((edge) => edge.target).join(', ') || '—'}</p>
              </>}
              <h4 className="mt-3 text-xs text-muted">{t('serviceMap.examples')}</h4>
              {examplesLoading && <SpinArea />}
              {examplesError && <div role="alert" className="text-xs text-error">{examplesError}<Button variant="link" size="sm" icon={<RotateCcw />} onClick={() => setExampleRetry((value) => value + 1)}>{t('serviceMap.retry')}</Button></div>}
              {!examplesLoading && !examplesError && !examples.length && <Empty description={t('serviceMap.noExamples')} />}
              {examplesLimited && <p className="text-xs text-warning">{t('serviceMap.examplesLimited')}</p>}
              {examples.map((row) => <div key={String(row.trace_id)} className="border-b border-border py-2">
                <Button variant="link" size="sm" icon={<Search />} onClick={() => setTrace(row)}>{String(row.trace_id)}</Button>
                <p className="truncate text-xs text-subtle">{formatDateTime(String(row.ts))} · {String(row.name)}</p>
              </div>)}
            </>}
          </section>
        </div>}
        <p className="text-xs text-subtle">{t('serviceMap.semantics')}</p>
      </>}
    </div>
    {execution && <TraceDetailDrawer row={trace} from={execution.from} to={execution.to} onClose={() => setTrace(null)}
      onFilterByValue={searchValue} onFilterOnlyValue={searchValue} />}
  </Page>;
}
