import { panelEditorSettings } from './panelDefinition';
import { displaySql } from './displaySql';
import { compilePieQuery, pieAggregations, pieGroups } from './pieQuery';
import { MetricPicker } from './MetricPicker';
import { SampleMatchedEvents } from './SampleMatchedEvents';
import { ExplorerResultStatus } from './ExplorerResultStatus';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { aggregateFields, tableFields } from './tableBuilder';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { useEffect, useRef, useState } from 'react';
import dayjs from 'dayjs';
import { Code, Palette, Play, RotateCcw, Save, Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, Chart, Drawer, Empty, Input, Select, Switch, TimeRangePicker, TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';
import { useCssTheme } from '@/design-system/theme';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import type { AggFunc, ChartSeries, ChartSource, PieDisplaySettings } from '@/api/chartTypes';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { seriesLabel } from './buildChartSql';
import { buildChartOption } from './chartOption';
import { DEFAULT_PIE_DISPLAY, pieData } from './pieDisplay';
import { DEFAULT_VALUE_FORMAT } from './barFormat';
import { ValueFormatFields } from './BarDisplayModal';
import { SaveChartModal } from './SaveChartModal';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { LuceneSearchHelp } from '@/pages/search/LuceneSearchHelp';

interface Execution {
  editor: import('@/api/chartTypes').ChartEditorDefinition;
  source: ChartSource;
  series: ChartSeries;
  range: TimeRange;
  sql: string;
  parameters: SearchSqlParameters;
}

/** Pie 保留独立草稿；切换 Tab 不丢失配置，保存只引用执行快照。 */
export function PieExplorer({ active, name, onNameChange }: { active: boolean; name: string; onNameChange: (value: string) => void }) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const saved = panelEditorSettings(workspace.initial);
  const initial = workspace.definition?.chartType === 'pie' ? workspace.definition : undefined;
  const theme = useCssTheme();
  const writable = canWriteRole(useAuthStore((state) => state.role));
  const [source, setSource] = useState<ChartSource>(initial?.source ?? 'logs');
  const [mode, setMode] = useState<'builder' | 'sql'>(initial?.mode ?? 'builder');
  const [series, setSeries] = useState<ChartSeries>(initial?.series[0] ?? { id: 'pie', aggFunc: 'count', field: '', where: '', alias: '' });
  const [group, setGroup] = useState(initial?.groupBy.join(', ') ?? '');
  const [syntax, setSyntax] = useState<'lucene' | 'sql'>(initial?.syntax ?? 'lucene');
  const [sqlText, setSqlText] = useState(initial?.mode === 'sql' ? workspace.urlState?.sql ?? workspace.initial?.query?.sql ?? workspace.initial?.sql ?? '' : '');
  const [sqlParameters, setSqlParameters] = useState<SearchSqlParameters>(initial ? workspace.urlState?.parameters ?? workspace.initial?.query?.parameters ?? {} : {});
  const [range, setRange] = useState<TimeRange>(() => workspace.range ?? (initial ? workspace.urlState?.range : undefined) ?? ({ from: dayjs().subtract(1, 'hour').toISOString(), to: dayjs().toISOString(), preset: '1h' }));
  const [settings, setSettings] = useState<PieDisplaySettings>(saved?.pieSettings ?? DEFAULT_PIE_DISPLAY);
  const [draft, setDraft] = useState<PieDisplaySettings | null>(null);
  const [seriesFormat, setSeriesFormat] = useState(false);
  const [limit, setLimit] = useState('');
  const [execution, setExecution] = useState<Execution | null>(null);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [samples, setSamples] = useState<AnalyticsQueryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [sampleError, setSampleError] = useState('');
  const [sqlOpen, setSqlOpen] = useState(false);
  const [samplesOpen, setSamplesOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const initialized = useRef(false);
  const generation = useRef(0);
  const pending = useRef(false);
  const sourceDrafts = useRef<Partial<Record<ChartSource, { series: ChartSeries; group: string; syntax: 'lucene' | 'sql'; sql: string; parameters: SearchSqlParameters; filters: { field: string; value: unknown }[] }>>>({});
  const [valueFilters, setValueFilters] = useState<{ field: string; value: unknown }[]>(initial?.valueFilters ?? []);

  useChartUrlState(active, {
    editor: { version: 1, chartType: 'pie', mode, source, series: [series], syntax, valueFilters, groupBy: pieGroups(group), formulas: [], granularity: 'auto' },
    range, sql: sqlText, parameters: sqlParameters,
  });

  async function run(next = { source, mode, series, group, syntax, sqlText, sqlParameters, range, valueFilters }) {
    const { source, mode, series, group, syntax, sqlText, sqlParameters, range, valueFilters } = next;
    if (pending.current) return;
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === range.preset);
    const to = preset ? dayjs() : dayjs(range.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(range.from);
    if (!from.isValid() || !to.isValid() || !from.isBefore(to)) { setError(t('search.badTimeInput')); return; }
    const executedRange = { ...range, from: from.toISOString(), to: to.toISOString() };
    let parameters: SearchSqlParameters = { ...sqlParameters };
    let sql = sqlText;
    let sampleSql: string | null = null;
    try {
      if (mode === 'builder') ({ sql, sampleSql, parameters } = compilePieQuery(source, series, group, syntax, valueFilters, to.diff(from)));
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
    const id = ++generation.current;
    pending.current = true;
    setSaveOpen(false);
    setLoading(true); setError(''); setSampleError(''); setResult(null); setSamples(null);
    setExecution({ editor: { version: 1, mode, source, chartType: 'pie', series: [{ ...series }], syntax, valueFilters, groupBy: pieGroups(group), formulas: [], granularity: 'auto' }, source, series: { ...series }, range: executedRange, sql, parameters });
    setRange(executedRange);
    try {
      const [chart, events] = await Promise.allSettled([
        searchSql({ sql, ...executedRange, parameters, rowLimit: 10000 }),
        sampleSql ? searchSql({ sql: sampleSql, ...executedRange, parameters, rowLimit: 500 }) : Promise.resolve(null),
      ]);
      if (generation.current !== id) return;
      if (chart.status === 'fulfilled') setResult(chart.value);
      else setError(chart.reason instanceof Error ? chart.reason.message : String(chart.reason));
      if (events.status === 'fulfilled') setSamples(events.value);
      else setSampleError(events.reason instanceof Error ? events.reason.message : String(events.reason));
    } finally { if (generation.current === id) { setLoading(false); pending.current = false; } }
  }

  useEffect(() => {
    let cancelled = false;
    if (active && workspace.mode !== 'panel' && !workspace.onApply && !initialized.current) queueMicrotask(() => {
      if (!cancelled) { initialized.current = true; void run(); }
    });
    return () => { cancelled = true; };
  }, [active]);
  useEffect(() => () => { generation.current++; }, []);
  useEffect(() => { if (!active) { setDraft(null); setSaveOpen(false); } }, [active]);
  const display = { ...settings, format: execution?.series.format ?? settings.format };
  const data = result && execution ? pieData(result, seriesLabel(execution.series), display) : null;
  const option = result && execution ? buildChartOption(result, 'pie', execution.range, [execution.series], workspace.initial?.showLegend ?? true, 'line', theme.border, display) : null;
  const invalidLimit = limit !== '' && (!/^\d+$/.test(limit) || Number(limit) < 1 || Number(limit) > 10000);
  const openSettings = (individual: boolean) => {
    setSeriesFormat(individual);
    setDraft(individual ? { format: series.format ?? settings.format } : { ...settings });
    setLimit(settings.seriesLimit == null ? '' : String(settings.seriesLimit));
  };

  function switchSource(next: ChartSource) {
    sourceDrafts.current[source] = { series, group, syntax, sql: sqlText, parameters: sqlParameters, filters: valueFilters };
    generation.current++; pending.current = false;
    setLoading(false); setResult(null); setSamples(null); setExecution(null); setError(''); setSampleError(''); setSaveOpen(false); setDraft(null);
    const restored = sourceDrafts.current[next];
    const nextSeries: ChartSeries = restored?.series ?? { id: 'pie', aggFunc: next === 'iot' || next === 'metrics' ? 'avg' : 'count', field: next === 'iot' ? 'num_value' : next === 'metrics' ? 'value' : '', where: '', alias: '', ...(next === 'metrics' ? { metricName: '' } : {}) };
    const nextSyntax = restored?.syntax ?? (next === 'logs' || next === 'traces' ? 'lucene' : 'sql');
    const parameters: SearchSqlParameters = { ...restored?.parameters };
    let sql = restored?.sql ?? '';
    if (mode === 'sql' && !sql) {
      try {
        const query = compilePieQuery(next, nextSeries, restored?.group ?? '', nextSyntax, restored?.filters ?? []);
        sql = query.sql;
        Object.assign(parameters, query.parameters);
      } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    }
    setSource(next); setSeries(nextSeries);
    setGroup(restored?.group ?? ''); setSyntax(nextSyntax);
    setSqlText(sql); setSqlParameters(parameters); setValueFilters(restored?.filters ?? []);
  }
  function filterSample(field: string, value: unknown, only = false, target = execution?.source ?? source) {
    if (!execution || target !== execution.source || execution.editor.mode !== 'builder') return;
    const nextSeries = { ...execution.series, where: only ? '' : execution.series.where };
    const filters = [...(only ? [] : execution.editor.valueFilters ?? []), { field, value }];
    const nextRange = { from: execution.range.from, to: execution.range.to };
    setSource(execution.source); setMode('builder'); setSeries(nextSeries); setGroup(execution.editor.groupBy.join(', ')); setSyntax(execution.editor.syntax ?? 'sql'); setValueFilters(filters); setRange(nextRange);
    void run({ source: execution.source, mode: 'builder', series: nextSeries, group: execution.editor.groupBy.join(', '), syntax: execution.editor.syntax ?? 'sql', sqlText: '', sqlParameters: {}, range: nextRange, valueFilters: filters });
  }
  const changed = Boolean(execution && (mode !== execution.editor.mode || JSON.stringify(series) !== JSON.stringify(execution.series) || pieGroups(group).join(',') !== execution.editor.groupBy.join(',') || syntax !== execution.editor.syntax || JSON.stringify(valueFilters) !== JSON.stringify(execution.editor.valueFilters ?? []) || range.from !== execution.range.from || range.to !== execution.range.to || mode === 'sql' && sqlText !== execution.sql));
  if (!active) return null;

  return <div data-testid="pie-explorer">
    <div className="flex flex-wrap items-center gap-2 border-b border-border/60 py-3">
      <label htmlFor="pie-name" className="text-muted">{t('charts.chartName')}</label>
      <Input id="pie-name" value={name} onChange={(event) => onNameChange(event.target.value)} placeholder={t('charts.chartNamePlaceholder')} maxLength={80} className="min-w-40 flex-1" />
      <Button variant={mode === 'builder' ? 'secondary' : 'ghost'} onClick={() => setMode('builder')}>Builder</Button>
      <Button variant={mode === 'sql' ? 'secondary' : 'ghost'} onClick={() => {
        if (!sqlText) {
          const parameters: SearchSqlParameters = {};
          try {
            const query = compilePieQuery(source, series, group, syntax, valueFilters);
            setSqlText(query.sql);
            Object.assign(parameters, query.parameters);
            setSqlParameters(parameters);
          } catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
        }
        setMode('sql');
      }}>SQL</Button>
    </div>
    <div className="flex items-center gap-3 border-b border-border/60 py-4">
      <span className="shrink-0 text-muted">{t('charts.dataSource')}</span>
      <div className="w-48 min-w-0"><Select aria-label={t('charts.dataSource')} value={source} onValueChange={(value) => switchSource(value as ChartSource)} options={[
        { value: 'logs', label: t('search.sourceLogs') }, { value: 'traces', label: t('search.sourceTraces') }, { value: 'metrics', label: t('charts.sourceMetrics') }, { value: 'sessions', label: t('charts.sourceSessions') }, { value: 'iot', label: t('charts.sourceMqtt') },
      ]} /></div>
    </div>
    {mode === 'builder' ? <div className="space-y-3 border-b border-border/60 py-3">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <span className="text-muted">A</span><label htmlFor="pie-alias">{t('charts.alias')}</label>
        <div className="w-44"><Input id="pie-alias" value={series.alias} onChange={(event) => setSeries({ ...series, alias: event.target.value })} placeholder={t('charts.seriesAlias')} maxLength={64} /></div>
        <Button variant="link" size="sm" icon={<Palette className="size-3.5" />} onClick={() => openSettings(true)}>{t('charts.seriesFormat')}</Button>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <div className="w-48 shrink-0"><Select aria-label={t('charts.pieAggregation')} value={series.aggFunc} onValueChange={(value) => {
          const aggFunc = value as AggFunc;
          const fields = aggregateFields(source, aggFunc);
          setSeries({ ...series, aggFunc, columnExpression: true, metricDelta: false, field: aggFunc === 'count' ? '' : fields.some((item) => item.field === series.field) ? series.field : fields[0]?.field ?? '' });
        }} options={pieAggregations(source).map((value) => ({ value, label: t(({ count: 'charts.aggCount', avg: 'charts.aggAvg', sum: 'charts.aggSum', min: 'charts.aggMin', max: 'charts.aggMax', count_distinct: 'charts.aggCountDistinct', p50: 'numberSources.median', p90: 'numberSources.p90', p95: 'charts.aggP95', p99: 'numberSources.p99', any: 'numberSources.any', custom: 'numberSources.custom' }[value])) }))} /></div>
        {source === 'metrics' && <MetricPicker index={0} value={series.metricName ?? ''} range={range} onChange={(metricName) => setSeries({ ...series, metricName, metricFilters: [] })} onFilter={(field, value) => setSeries({ ...series, metricFilters: [...(series.metricFilters ?? []), { field, value }] })} />}
        {series.aggFunc !== 'count' && (source === 'sessions' ? <div className="w-44"><Select aria-label={t('charts.fieldPlaceholder')} value={series.field} options={aggregateFields(source, series.aggFunc).map((item) => ({ value: item.field, label: item.field }))} onValueChange={(field) => setSeries({ ...series, field })} /></div> : <div className="w-56"><WhereSuggestInput domain={source} syntax="sql" value={series.field} onChange={(field) => setSeries({ ...series, field, columnExpression: true })} placeholder={t('charts.barColumn')} ariaLabel={t('numberSources.column', { index: 1 })} fieldsOnly testId="pie-column" from={null} to={null} denoise={false} onRun={() => void run()} /></div>)}
        {source === 'metrics' && !['count', 'count_distinct'].includes(series.aggFunc) && <label className="flex items-center gap-2"><Switch checked={series.metricDelta ?? false} onChange={(metricDelta) => setSeries({ ...series, metricDelta })} aria-label={t('numberSources.delta', { index: 1 })} />{t('numberSources.deltaLabel')}</label>}
        <label htmlFor="pie-where" className="text-muted">{t('charts.where')}</label>
        <div className="w-24 shrink-0"><Select aria-label={t('charts.eventSyntax')} value={syntax} onValueChange={(value) => setSyntax(value as 'lucene' | 'sql')} options={[{ value: 'lucene', label: 'Lucene' }, { value: 'sql', label: 'SQL' }]} /></div>
        <div className="min-w-48 flex-1"><WhereSuggestInput domain={source} syntax={syntax} value={series.where} onChange={(where) => setSeries({ ...series, where })} onRun={() => void run()} testId="pie-where" ariaLabel={t('charts.where')} from={range.from} to={range.to} denoise={false} placeholder={t(syntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${source}` : 'charts.wherePlaceholder')} /></div>
        <label htmlFor="pie-group" className="text-muted">{t('charts.groupBy')}</label>
        <div className="w-48"><Input id="pie-group" value={group} onChange={(event) => setGroup(event.target.value)} placeholder={t('charts.groupByPlaceholder')} /></div>
      </div>
      <div className="flex flex-wrap gap-2">{tableFields(source).filter((item) => item.field !== 'value').map((item) => <Button key={item.field} size="sm" variant="link" onClick={() => { const groups = pieGroups(group); if (!groups.includes(item.field)) setGroup([...groups, item.field].join(', ')); }}>{item.field}</Button>)}</div>
      <p className="text-xs text-muted">{t('pieSources.groupHint')}</p>
      {source === 'metrics' && <div className="space-y-1 text-xs text-muted">
        {series.metricName === undefined && <p>{t('numberSources.legacyMetrics')}</p>}
        {series.metricDelta && <p>{t('numberSources.deltaHint')}</p>}
        {(series.metricFilters ?? []).map((filter, index) => <Button key={index} variant="link" size="sm" onClick={() => setSeries({ ...series, metricFilters: series.metricFilters?.filter((_, i) => i !== index) })}>{filter.field}: {filter.value} · {t('numberSources.removeFilter')}</Button>)}
      </div>}
      {source === 'traces' && <p className="text-xs text-muted">{t('numberSources.tracesHint')}</p>}
      {source === 'sessions' && <p className="text-xs text-muted">{t('pieSources.sessionsHint')}</p>}
      {valueFilters.length > 0 && <div className="flex flex-wrap gap-2">{valueFilters.map((item, index) => <Button key={index} size="sm" onClick={() => setValueFilters(valueFilters.filter((_, i) => i !== index))}>{item.field}: {String(item.value)} · {t('common.delete')}</Button>)}</div>}
      {syntax === 'lucene' && <LuceneSearchHelp source={source} value={series.where} context="chart" />}
    </div> : <div className="py-3"><SqlEditor value={sqlText} onChange={setSqlText} onRun={() => void run()} /></div>}
    <div className="flex justify-end py-3"><Button icon={<Palette className="size-3.5" />} onClick={() => openSettings(false)}>{t('charts.displaySettings')}</Button></div>
    <div className="flex flex-wrap items-center justify-end gap-2 pb-3">
      {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange testId="pie-time-range" />}
      <Button variant="primary" icon={<Play className="size-3.5" />} disabled={loading} testId="pie-run" onClick={() => void run()}>{loading ? t('common.loading') : t('charts.run')}</Button>
      {writable && <Button icon={<Save className="size-3.5" />} disabled={!result || loading || !!error} onClick={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</Button>}
    </div>
    {execution && <ExplorerResultStatus source={execution.source} range={execution.range} result={result} changed={changed} />}
    {error && <p role="alert" className="py-2 text-error">{error}</p>}
    {result?.truncated && <p role="status" className="py-2 text-warning">{t('charts.resultTruncated')}</p>}
    {data?.limited && <p className="py-2 text-muted">{t('charts.pieLimited')}</p>}
    <div className="h-[400px] min-w-0">
      {loading ? <Empty description={t('common.loading')} className="py-24" /> : data?.invalid ? <p role="alert" className="py-24 text-center text-warning">{t('charts.pieNegative')}</p> : option ? <Chart option={option} minHeight={400} /> : <Empty description={result && !result.columns.includes('value') ? t('charts.pieValueRequired') : t('charts.pieNoPositive')} className="py-24" />}
    </div>
    <SampleMatchedEvents source={execution?.source ?? source} range={execution?.range ?? range} mode={execution?.editor.mode ?? mode} result={samples} loading={loading} error={sampleError} generation={generation.current} open={samplesOpen} onOpenChange={setSamplesOpen} onFilter={filterSample} />
    <button type="button" className="flex w-full items-center justify-between border-t border-border py-3" aria-expanded={sqlOpen} onClick={() => setSqlOpen(!sqlOpen)}>{t('charts.generatedSql')}<Code className="size-3.5" /></button>
    {sqlOpen && execution && <SqlEditor value={displaySql(execution.sql, execution.parameters)} readOnly height="240px" />}
    {draft && <Drawer open title={t(seriesFormat ? 'charts.seriesFormat' : 'charts.displaySettings')} onOpenChange={(open) => { if (!open) setDraft(null); }} width={440} footer={<>
      <Button icon={<RotateCcw className="size-3.5" />} onClick={() => { setDraft({ format: { ...DEFAULT_VALUE_FORMAT } }); setLimit(''); }}>{t('charts.resetDefaults')}</Button>
      <Button variant="primary" icon={<Check className="size-3.5" />} disabled={!seriesFormat && invalidLimit} onClick={() => {
        if (seriesFormat) { setSeries({ ...series, format: draft.format }); setExecution((current) => current ? { ...current, series: { ...current.series, format: draft.format } } : current); }
        else setSettings({ ...draft, seriesLimit: limit === '' ? undefined : Number(limit) });
        setDraft(null);
      }}>{t('charts.applySettings')}</Button>
    </>}>
      <div className="space-y-5">
        {!seriesFormat && <label className="block space-y-2 border-b border-border pb-4"><span>{t('charts.seriesLimit')}</span><p className="text-xs text-muted">{t('charts.seriesLimitHint')}</p><Input value={limit} onChange={(event) => setLimit(event.target.value)} aria-label={t('charts.seriesLimit')} invalid={invalidLimit} placeholder={t('charts.seriesLimitPlaceholder')} />{invalidLimit && <p role="alert" className="text-error">{t('charts.invalidSeriesLimit')}</p>}</label>}
        <ValueFormatFields value={draft.format} onChange={(format) => setDraft({ ...draft, format })} />
        {!seriesFormat && <p className="rounded border border-border p-3 text-muted">{t('charts.seriesFormatOverride')}</p>}
      </div>
    </Drawer>}
    {(saveOpen || workspace.onApplyActionChange) && execution && result && !loading && !error && <SaveChartModal editor={{ ...execution.editor, series: [execution.series] }} open={saveOpen} onOpenChange={setSaveOpen} name={name} chartType="pie" source={execution.source} series={[execution.series]} range={execution.range} sql={execution.sql} parameters={execution.parameters} pieSettings={settings} display="line" showLegend={workspace.initial?.showLegend ?? true} autoGranularity={false} />}
  </div>;
}
