import { panelEditorSettings } from './panelDefinition';
import { displaySql } from './displaySql';
import { compileChartWhere } from './chartWhere';
import { SampleMatchedEvents } from './SampleMatchedEvents';
import { ExplorerResultStatus } from './ExplorerResultStatus';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { MetricPicker } from './MetricPicker';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { useEffect, useMemo, useRef, useState } from 'react';
import dayjs from 'dayjs';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Code, MoreHorizontal, Play, Save, RotateCcw, X } from 'lucide-react';
import { Button, Chart, DropdownMenu, Input, Popover, Select, Switch, TimeRangePicker, TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import type { ChartSource, HeatmapConfig } from '@/api/chartTypes';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { buildChartSql } from './buildChartSql';
import { buildDistributionOption, buildHeatmapSampleSql, distributionGrid, heatmapZoomRange, DEFAULT_HEATMAP, type HeatmapCell } from './heatmap';
import { formatDateTime, formatDurationUs } from '@/utils/format';
import { SaveChartModal } from './SaveChartModal';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { LuceneSearchHelp } from '@/pages/search/LuceneSearchHelp';

interface Props { active: boolean; name: string; onNameChange: (name: string) => void }
interface Execution {
  editor: import('@/api/chartTypes').ChartEditorDefinition;
  source: ChartSource;
  config: HeatmapConfig;
  range: TimeRange;
  sql: string;
  panelSql: string;
  sampleSql: string;
  parameters: SearchSqlParameters;
}

/** 与其它 Tab 隔离编辑态；保存只使用最后一次成功执行的快照. */
export function HeatmapExplorer({ active, name, onNameChange }: Props) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const saved = panelEditorSettings(workspace.initial);
  const initial = workspace.definition?.chartType === 'heatmap' ? workspace.definition : undefined;
  const [source, setSource] = useState<ChartSource>(initial?.source ?? 'traces');
  const [config, setConfig] = useState<HeatmapConfig>(saved?.heatmap ?? (initial ? workspace.urlState?.heatmap : undefined) ?? { ...DEFAULT_HEATMAP, field: initial?.source === 'metrics' ? 'value' : 'duration_us', scale: initial?.source === 'metrics' ? 'linear' : 'log' });
  const [where, setWhere] = useState(initial?.where ?? '');
  const [syntax, setSyntax] = useState<'lucene' | 'sql'>(initial?.syntax ?? 'lucene');
  const [valueFilters, setValueFilters] = useState<{ field: string; value: unknown }[]>(initial?.valueFilters ?? []);
  const [range, setRange] = useState<TimeRange>(() => {
    if (workspace.range) return workspace.range;
    if (initial && workspace.urlState) return workspace.urlState.range;
    const to = dayjs();
    return { from: to.subtract(1, 'hour').toISOString(), to: to.toISOString(), preset: '1h' };
  });
  const [legend, setLegend] = useState(workspace.initial?.showLegend ?? true);
  const [execution, setExecution] = useState<Execution | null>(null);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [samples, setSamples] = useState<AnalyticsQueryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [sampleError, setSampleError] = useState('');
  const [selected, setSelected] = useState<HeatmapCell | null>(null);
  const [cellSamples, setCellSamples] = useState<AnalyticsQueryResponse | null>(null);
  const [cellLoading, setCellLoading] = useState(false);
  const [cellError, setCellError] = useState('');
  const [history, setHistory] = useState<TimeRange[]>([]);
  const sampleGeneration = useRef(0);
  const [sqlOpen, setSqlOpen] = useState(false);
  const [samplesOpen, setSamplesOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const [metricName, setMetricName] = useState(initial?.series[0]?.metricName ?? '');
  const sourceDrafts = useRef<Partial<Record<ChartSource, { config: HeatmapConfig; where: string; syntax: 'lucene' | 'sql'; filters: { field: string; value: unknown }[]; metricName: string }>>>({});
  const generation = useRef(0);
  const initialized = useRef(false);
  const writable = canWriteRole(useAuthStore((state) => state.role));

  useChartUrlState(active, {
    editor: { version: 1, chartType: 'heatmap', mode: 'builder', source, series: [{ id: 'heatmap', aggFunc: 'count', field: '', where: '', alias: '', metricName }], formulas: [], groupBy: [], granularity: 'auto', where, syntax, valueFilters },
    range, heatmap: config,
  });

  async function run(next = { source, config, where, syntax, valueFilters, range, metricName }) {
    const { source, config, where, syntax, valueFilters, range, metricName } = next;
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === range.preset);
    const to = preset ? dayjs() : dayjs(range.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(range.from);
    if (!from.isValid() || !to.isValid() || !from.isBefore(to)) { setError(t('search.badTimeInput')); return; }
    const executedRange = { ...range, from: from.toISOString(), to: to.toISOString() };
    const parameters: SearchSqlParameters = {};
    let series;
    try {
      series = compileChartWhere(source, [{ id: 'heatmap', aggFunc: 'count', field: '', where, alias: '', whereSyntax: source === 'traces' ? syntax : 'sql', metricName }], valueFilters, parameters);
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
    const input = { domain: source, chartType: 'heatmap' as const, heatmap: config, seriesList: series, groupBy: [], granularity: 'auto' as const, rangeMs: to.diff(from), parameters };
    let sql: string;
    let panelSql: string;
    let sampleSql: string;
    try {
      sql = buildChartSql(input);
      panelSql = buildChartSql({ ...input, bucketParameter: true });
      sampleSql = buildChartSql({ ...input, chartType: 'search' });
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
    const id = ++generation.current;
    sampleGeneration.current++; setSelected(null); setCellSamples(null); setCellLoading(false); setCellError('');
    setSaveOpen(false);
    setLoading(true); setError(''); setSampleError(''); setResult(null); setSamples(null);
    setExecution({ editor: { version: 1, mode: 'builder', source, chartType: 'heatmap', series: [{ id: 'heatmap', aggFunc: 'count', field: '', where: '', alias: '', metricName }], formulas: [], groupBy: [], granularity: 'auto', where, syntax, valueFilters }, source, config: { ...config }, range: executedRange, sql, panelSql, sampleSql, parameters });
    setRange(executedRange);
    try {
      const [chart, events] = await Promise.allSettled([
        searchSql({ sql, ...executedRange, parameters, rowLimit: 10000 }),
        searchSql({ sql: sampleSql, ...executedRange, parameters, rowLimit: 500 }),
      ]);
      if (generation.current !== id) return;
      if (chart.status === 'fulfilled') setResult(chart.value);
      else setError(chart.reason instanceof Error ? chart.reason.message : String(chart.reason));
      if (events.status === 'fulfilled') setSamples(events.value);
      else setSampleError(events.reason instanceof Error ? events.reason.message : String(events.reason));
    } finally { if (generation.current === id) setLoading(false); }
  }

  useEffect(() => {
    let cancelled = false;
    if (active && workspace.mode !== 'panel' && !workspace.onApply && !initialized.current) queueMicrotask(() => {
      if (!cancelled) { initialized.current = true; void run(); }
    });
    return () => { cancelled = true; };
  }, [active]);
  useEffect(() => () => { generation.current++; sampleGeneration.current++; }, []);
  useEffect(() => { if (!active) setSaveOpen(false); }, [active]);

  const grid = useMemo(() => result && execution ? distributionGrid(result, execution.range, execution.config) : null, [result, execution]);
  const option = useMemo(() => result && execution ? buildDistributionOption(result, execution.range, { ...execution.config, colorScale: config.colorScale }, legend, {
    low: t('charts.heatmapLow'), high: t('charts.heatmapHigh'), count: t('charts.heatmapCount'),
    duration: t('charts.heatmapDuration'), value: t('charts.heatmapValue'), noData: t('charts.noDataHint'),
  }, true, selected) : null, [result, execution, config.colorScale, legend, selected, t]);

  async function selectCell(cell: HeatmapCell) {
    if (!execution || loading) return;
    const id = ++sampleGeneration.current;
    const chartId = generation.current;
    setSelected(cell); setCellSamples(null); setCellLoading(true); setCellError(''); setSamplesOpen(true);
    const parameters = { ...execution.parameters };
    const sql = buildHeatmapSampleSql(execution.sampleSql, execution.config, cell, parameters);
    try {
      const response = await searchSql({ sql, ...execution.range, parameters, rowLimit: 500 });
      if (sampleGeneration.current === id && generation.current === chartId) setCellSamples(response);
    } catch (err) {
      if (sampleGeneration.current === id && generation.current === chartId) setCellError(err instanceof Error ? err.message : String(err));
    } finally { if (sampleGeneration.current === id) setCellLoading(false); }
  }
  function clearCell() {
    sampleGeneration.current++; setSelected(null); setCellLoading(false); setCellError('');
  }
  function runExecuted(nextRange: TimeRange) {
    if (!execution) return;
    const next = { source: execution.source, config: { ...execution.config, colorScale: config.colorScale }, where: execution.editor.where ?? '',
      syntax: execution.editor.syntax ?? 'sql' as const, valueFilters: execution.editor.valueFilters ?? [], range: nextRange,
      metricName: execution.editor.series[0]?.metricName ?? '' };
    setSource(next.source); setConfig(next.config); setWhere(next.where); setSyntax(next.syntax); setValueFilters(next.valueFilters); setMetricName(next.metricName);
    void run(next);
  }
  function zoom(start: number, end: number) {
    if (!grid || !execution || loading) return;
    const next = heatmapZoomRange(grid, execution.range, start, end);
    if (!next) return;
    setHistory((items) => [...items, { from: execution.range.from, to: execution.range.to }]);
    runExecuted(next);
  }
  const events = {
    click: (event: { value?: unknown }) => {
      if (!Array.isArray(event.value)) return;
      const [x, y] = event.value;
      const cell = grid?.cells.find((item) => item.x === x && item.y === y);
      if (cell) void selectCell(cell);
    },
    brushEnd: (event: unknown) => {
      const area = (event as { areas?: { coordRange?: number[] }[] }).areas?.[0];
      if (area?.coordRange) zoom(area.coordRange[0], area.coordRange[1]);
    },
    datazoom: (event: unknown) => {
      const raw = event as { batch?: { start?: number; end?: number; startValue?: number; endValue?: number }[]; start?: number; end?: number; startValue?: number; endValue?: number };
      const item = raw.batch?.[0] ?? raw;
      if (!grid) return;
      zoom(item.startValue ?? (item.start ?? 0) / 100 * (grid.times.length - 1),
        item.endValue ?? (item.end ?? 100) / 100 * (grid.times.length - 1));
    },
  };
  function switchSource(next: ChartSource) {
    sourceDrafts.current[source] = { config, where, syntax, filters: valueFilters, metricName };
    sampleGeneration.current++; setSelected(null); setCellLoading(false); setHistory([]);
    generation.current++; setLoading(false); setResult(null); setSamples(null); setExecution(null); setError(''); setSampleError(''); setSaveOpen(false);
    const restored = sourceDrafts.current[next];
    setSource(next); setConfig(restored?.config ?? { ...DEFAULT_HEATMAP, field: next === 'traces' ? 'duration_us' : 'value', scale: next === 'traces' ? 'log' : 'linear' }); setWhere(restored?.where ?? ''); setSyntax(restored?.syntax ?? (next === 'traces' ? 'lucene' : 'sql')); setValueFilters(restored?.filters ?? []); setMetricName(restored?.metricName ?? '');
  }
  function filterSample(field: string, value: unknown, only = false, target = execution?.source ?? source) {
    if (!execution || target !== execution.source) return;
    const filters = [...(only ? [] : execution.editor.valueFilters ?? []), { field, value }];
    const next = { source: execution.source, config: execution.config, where: only ? '' : execution.editor.where ?? '', syntax: execution.editor.syntax ?? 'sql' as const, valueFilters: filters, range: { from: execution.range.from, to: execution.range.to }, metricName: execution.editor.series[0]?.metricName ?? '' };
    setSource(next.source); setConfig(next.config); setWhere(next.where); setSyntax(next.syntax); setValueFilters(filters); setMetricName(next.metricName); setRange(next.range); void run(next);
  }
  const changed = Boolean(execution && (where !== execution.editor.where || syntax !== execution.editor.syntax || (config.bins !== execution.config.bins || config.scale !== execution.config.scale) || JSON.stringify(valueFilters) !== JSON.stringify(execution.editor.valueFilters) || metricName !== (execution.editor.series[0]?.metricName ?? '') || range.from !== execution.range.from || range.to !== execution.range.to));
  if (!active) return null;
  return <div data-testid="heatmap-explorer">
    <div className="flex items-center gap-3 border-b border-border/60 py-3">
      <label htmlFor="heatmap-name" className="shrink-0 text-muted">{t('charts.chartName')}</label>
      <Input id="heatmap-name" value={name} onChange={(event) => onNameChange(event.target.value)} placeholder={t('charts.chartNamePlaceholder')} maxLength={80} className="flex-1" />
    </div>
    <div className="flex items-center gap-3 py-4">
      <span className="shrink-0 text-muted">{t('charts.dataSource')}</span>
      <div className="w-52 shrink-0"><Select aria-label={t('charts.dataSource')} value={source} onValueChange={(value) => switchSource(value as ChartSource)} options={[{ value: 'traces', label: t('search.sourceTraces') }, { value: 'metrics', label: t('charts.sourceMetrics') }]} /></div>
    </div>
    <div className="flex items-center gap-2 border-b border-border/60 pb-3">
      <div className="w-28 shrink-0"><Select aria-label={t('charts.heatmapFilterLanguage')} value={syntax} onValueChange={(value) => setSyntax(value as 'lucene' | 'sql')} options={source === 'traces' ? [{ value: 'lucene', label: 'Lucene' }, { value: 'sql', label: 'SQL' }] : [{ value: 'sql', label: 'SQL' }]} /></div>
      <div className="min-w-0 flex-1"><WhereSuggestInput domain={source} syntax={syntax} value={where} onChange={setWhere} onRun={() => void run()} testId="heatmap-where" ariaLabel={t('charts.where')} from={range.from} to={range.to} denoise={false} placeholder={t(syntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${source}` : 'charts.wherePlaceholder')} /></div>
    </div>
    {source === 'metrics' && <MetricPicker value={metricName} range={range} index={0} onChange={(value) => { setMetricName(value); setValueFilters([]); }} onFilter={(field, value) => setValueFilters([...valueFilters, { field, value }])} />}
    {syntax === 'lucene' && source === 'traces' && <LuceneSearchHelp source={source} value={where} context="chart" />}
    {valueFilters.length > 0 && <div className="flex flex-wrap gap-2 py-2">{valueFilters.map((filter, index) => <Button key={index} size="sm" onClick={() => setValueFilters((filters) => filters.filter((_, i) => i !== index))}>{filter.field}: {String(filter.value)} · {t('common.delete')}</Button>)}</div>}
    <div className="flex justify-end py-3">
      <Popover.Root><Popover.Trigger asChild><Button>{t('charts.displaySettings')}</Button></Popover.Trigger>
        <Popover.Content className="w-64 space-y-3">
          <p className="text-xs text-muted">{source === 'traces' ? t('charts.heatmapDuration') : t('charts.heatmapValue')}</p>
          <label className="flex items-center justify-between gap-2">{t('charts.showLegend')}<Switch checked={legend} onChange={setLegend} aria-label={t('charts.showLegend')} /></label>
          <label className="block space-y-1"><span>{t('charts.heatmapBins')}</span><Select aria-label={t('charts.heatmapBins')} value={String(config.bins)} onValueChange={(value) => setConfig({ ...config, bins: Number(value) })} options={[10, 20, 40, 60, 80].map((value) => ({ value: String(value), label: String(value) }))} /></label>
          <label className="block space-y-1"><span>{t('charts.heatmapScale')}</span><Select aria-label={t('charts.heatmapScale')} value={config.scale ?? 'linear'} onValueChange={(value) => setConfig({ ...config, scale: value as 'linear' | 'log' })} options={['linear', 'log'].map((value) => ({ value, label: t('charts.heatmapScale_' + value) }))} /></label>
          <label className="block space-y-1"><span>{t('charts.heatmapColorScale')}</span><Select aria-label={t('charts.heatmapColorScale')} value={config.colorScale ?? 'linear'} onValueChange={(value) => setConfig({ ...config, colorScale: value as 'linear' | 'log' })} options={['linear', 'log'].map((value) => ({ value, label: t('charts.heatmapScale_' + value) }))} /></label>
          <p className="text-xs text-muted">{t('charts.heatmapScaleHint')}</p>
        </Popover.Content>
      </Popover.Root>
    </div>
    <div className="flex flex-wrap items-center justify-end gap-2 pb-3">
      {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange testId="heatmap-time-range" />}
      <Button variant="primary" icon={<Play className="size-3.5" />} disabled={loading} testId="heatmap-run" onClick={() => void run()}>{loading ? t('common.loading') : t('charts.run')}</Button>
      <DropdownMenu.Root><DropdownMenu.Trigger asChild><Button icon={<MoreHorizontal className="size-3.5" />}>{t('charts.actions')}</Button></DropdownMenu.Trigger>
        <DropdownMenu.Content align="end">
          {writable && <DropdownMenu.Item disabled={!result || loading || !!error} icon={<Save className="size-3.5" />} onSelect={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</DropdownMenu.Item>}
          <DropdownMenu.Item disabled={!execution} icon={<Code className="size-3.5" />} onSelect={() => setSqlOpen(true)}>{t('charts.previewSql')}</DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    </div>
    {execution && <ExplorerResultStatus source={execution.source} range={execution.range} result={result} changed={changed} />}
    {result?.truncated && <p role="status" className="text-warning">{t('charts.resultTruncated')}</p>}
    {error && <p role="alert" className="py-3 text-error">{error}</p>}
    <div className="flex items-center justify-between gap-2 py-2 text-xs text-muted">
      <span>{t('charts.heatmapInteractionHint')}</span>
      {history.length > 0 && <Button size="sm" icon={<RotateCcw className="size-3.5" />} disabled={loading} onClick={() => {
        const previous = history[history.length - 1]; setHistory((items) => items.slice(0, -1)); runExecuted(previous);
      }}>{t('charts.heatmapRestore')}</Button>}
    </div>
    <div className="h-[400px] min-w-0" aria-label={t('charts.tabHeatmap')}>
      {loading ? <p role="status" className="py-12 text-center text-muted">{t('common.loading')}</p> : option ? <Chart option={option} minHeight={400} onEvents={events} /> : <p className="py-12 text-center text-muted">{t('charts.noDataHint')}</p>}
    </div>
    {selected && <div className="flex flex-wrap items-center gap-2 border-t border-border py-2 text-xs"><span className="font-mono">{formatDateTime(selected.from)} – {formatDateTime(selected.to)} · {execution?.config.field === 'duration_us' ? `${formatDurationUs(selected.min)} – ${formatDurationUs(selected.max)}` : `${selected.min} – ${selected.max}`} · {t('charts.heatmapCount')}: {selected.count}</span><Button size="sm" icon={<X className="size-3.5" />} onClick={clearCell}>{t('charts.heatmapClearSelection')}</Button></div>}
    <SampleMatchedEvents source={execution?.source ?? source} range={selected ?? execution?.range ?? range} mode="builder" result={selected ? cellSamples : samples} loading={selected ? cellLoading : loading} error={selected ? cellError : sampleError} generation={generation.current + sampleGeneration.current} open={samplesOpen} onOpenChange={setSamplesOpen} onFilter={filterSample} />
    <button type="button" className="flex w-full items-center justify-between border-t border-border py-3" aria-expanded={sqlOpen} onClick={() => setSqlOpen(!sqlOpen)}>{t('charts.generatedSql')}<ChevronDown className="size-3.5" /></button>
    {sqlOpen && execution && <SqlEditor value={displaySql(execution.sql, execution.parameters)} readOnly height="280px" />}
    {(saveOpen || workspace.onApplyActionChange) && execution && result && !loading && !error && <SaveChartModal editor={execution.editor} open={saveOpen} onOpenChange={setSaveOpen} name={name} chartType="heatmap" source={execution.source} heatmap={{ ...execution.config, colorScale: config.colorScale }} sql={execution.panelSql} parameters={execution.parameters} range={execution.range} series={[]} display="line" showLegend={legend} autoGranularity />}

  </div>;
}
