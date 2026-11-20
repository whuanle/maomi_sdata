import { panelEditorSettings } from './panelDefinition';
import { displaySql } from './displaySql';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { useEffect, useRef, useState } from 'react';
import dayjs from 'dayjs';
import { Code, Palette, Play, Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, Chart, Empty, Input, Select, TimeRangePicker, TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';
import { useCssTheme } from '@/design-system/theme';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import type { AggFunc, BarDisplaySettings, ChartSeries, ChartSource } from '@/api/chartTypes';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { buildChartSql, seriesLabel } from './buildChartSql';
import { BAR_FIELDS } from './barQuery';
import { barData, buildBarOption } from './barDisplay';
import { DEFAULT_VALUE_FORMAT } from './barFormat';
import { BarDisplayModal } from './BarDisplayModal';
import { SaveChartModal } from './SaveChartModal';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { compileChartWhere } from './chartWhere';
import { SampleMatchedEvents } from './SampleMatchedEvents';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { LuceneSearchHelp } from '@/pages/search/LuceneSearchHelp';

interface Execution {
  editor: import('@/api/chartTypes').ChartEditorDefinition;
  source: ChartSource;
  series: ChartSeries;
  groupBy: string[];
  orderBy: string;
  settings: BarDisplaySettings;
  range: TimeRange;
  sql: string;
  parameters: SearchSqlParameters;
}

/** 类别 Bar 保留独立草稿，查询修改仅在 Run 后进入执行快照. */
export function BarExplorer({ active, name, onNameChange }: { active: boolean; name: string; onNameChange: (value: string) => void }) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const saved = panelEditorSettings(workspace.initial);
  const initial = workspace.definition?.chartType === 'bar' ? workspace.definition : undefined;
  const theme = useCssTheme();
  const writable = canWriteRole(useAuthStore((state) => state.role));
  const [source, setSource] = useState<ChartSource>(initial?.source ?? 'logs');
  const [mode, setMode] = useState<'builder' | 'sql'>(initial?.mode ?? 'builder');
  const [series, setSeries] = useState<ChartSeries>(initial?.series[0] ?? { id: 'bar', aggFunc: 'count', field: '', where: '', alias: '' });
  const [syntax, setSyntax] = useState<'lucene' | 'sql'>(initial?.syntax ?? 'lucene');
  const [whereDrafts, setWhereDrafts] = useState({ lucene: '', sql: '' });
  const [groups, setGroups] = useState<string[]>(initial?.groupBy ?? []);
  const [sortField, setSortField] = useState(initial?.orderBy?.split(' ')[0] ?? 'value');
  const [direction, setDirection] = useState(initial?.orderBy?.split(' ')[1] ?? 'DESC');
  const [sqlText, setSqlText] = useState(initial?.mode === 'sql' ? workspace.urlState?.sql ?? workspace.initial?.query?.sql ?? workspace.initial?.sql ?? '' : '');
  const [sqlParameters, setSqlParameters] = useState<SearchSqlParameters>(initial ? workspace.urlState?.parameters ?? workspace.initial?.query?.parameters ?? {} : {});
  const [range, setRange] = useState<TimeRange>(() => workspace.range ?? (initial ? workspace.urlState?.range : undefined) ?? ({ from: dayjs().subtract(1, 'hour').toISOString(), to: dayjs().toISOString(), preset: '1h' }));
  const [settings, setSettings] = useState<BarDisplaySettings>(saved?.barSettings ?? { format: DEFAULT_VALUE_FORMAT, ...(initial ? { seriesLimit: workspace.urlState?.seriesLimit } : {}) });
  const [formatOpen, setFormatOpen] = useState<'chart' | 'series' | null>(null);
  const [execution, setExecution] = useState<Execution | null>(null);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [samples, setSamples] = useState<AnalyticsQueryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [sampleError, setSampleError] = useState('');
  const [sqlOpen, setSqlOpen] = useState(false);
  const [sampleExecution, setSampleExecution] = useState<Execution | null>(null);
  const [queryGeneration, setQueryGeneration] = useState(0);
  const [valueFilters, setValueFilters] = useState(initial?.valueFilters ?? []);
  const [saveOpen, setSaveOpen] = useState(false);
  const initialized = useRef(false);
  const generation = useRef(0);
  const pending = useRef(false);
  const fields = BAR_FIELDS[source];
  const columnDraft = useRef('');

  useChartUrlState(active, {
    editor: { version: 1, chartType: 'bar', mode, source, series: [series], syntax, groupBy: groups, orderBy: sortField + ' ' + direction, formulas: [], granularity: 'auto', valueFilters },
    range, sql: sqlText, parameters: sqlParameters, seriesLimit: settings.seriesLimit,
  });

  const current = { source, mode, series, syntax, groups, range, settings, valueFilters };
  const filterSamples = (field: string, value: unknown, only = false, target = sampleExecution?.source ?? source) => {
    if (!sampleExecution || pending.current) return;
    const sameSource = target === sampleExecution.source;
    const context: Execution = sameSource ? sampleExecution : {
      ...sampleExecution, source: target, series: { id: 'bar', aggFunc: 'count', field: '', where: '', alias: '' }, groupBy: [], orderBy: 'value DESC',
      editor: { ...sampleExecution.editor, syntax: 'lucene', valueFilters: [] },
    };
    const previous = sameSource ? context.editor.valueFilters ?? [] : [];
    const filters = [...(only ? previous.filter((item) => item.field !== field) : previous), { field, value }];
    restoreSample(context, filters);
  };
  function restoreSample(context: Execution, filters: { field: string; value: unknown }[]) {
    setMode('builder'); setSource(context.source); setSeries(context.series); setSyntax(context.editor.syntax ?? 'sql');
    setGroups(context.groupBy); setSortField(context.orderBy.split(' ')[0]); setDirection(context.orderBy.split(' ')[1]);
    setRange({ from: context.range.from, to: context.range.to }); setSettings(context.settings); setValueFilters(filters);
    void run({ context: { ...context, range: { from: context.range.from, to: context.range.to } }, filters });
  }

  async function run(override?: { context: Execution; filters: { field: string; value: unknown }[] }) {
    if (pending.current) return;
    const source = override?.context.source ?? current.source;
    const mode = override ? 'builder' : current.mode;
    const series = override?.context.series ?? current.series;
    const syntax = override?.context.editor.syntax ?? current.syntax;
    const groups = override?.context.groupBy ?? current.groups;
    const range = override?.context.range ?? current.range;
    const settings = override?.context.settings ?? current.settings;
    const valueFilters = override?.filters ?? current.valueFilters;
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === range.preset);
    const to = preset ? dayjs() : dayjs(range.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(range.from);
    if (!from.isValid() || !to.isValid() || !from.isBefore(to)) { setError(t('search.badTimeInput')); return; }
    const executedRange = { ...range, from: from.toISOString(), to: to.toISOString() };
    const parameters: SearchSqlParameters = mode === 'sql' ? { ...sqlParameters } : {};
    const orderBy = override?.context.orderBy ?? sortField + ' ' + direction;
    let sql: string;
    let sampleSql: string | null;
    try {
      const filteredSeries = mode === 'builder' ? compileChartWhere(source, [{ ...series, whereSyntax: syntax }], valueFilters, parameters) : [series];
      const input = { domain: source, chartType: 'bar' as const, seriesList: filteredSeries, groupBy: groups, granularity: 'auto' as const, rangeMs: to.diff(from), parameters, orderBy, seriesLimit: settings.seriesLimit };
      sql = mode === 'sql' ? sqlText : buildChartSql(input);
      sampleSql = mode === 'sql' ? null : buildChartSql({ ...input, chartType: 'search' });
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
    const id = ++generation.current;
    const submitted: Execution = { editor: { version: 1, mode, source, chartType: 'bar', series: [{ ...series }], syntax, groupBy: [...groups], orderBy, formulas: [], granularity: 'auto', valueFilters }, source, series: { ...series }, groupBy: [...groups], orderBy, settings: { ...settings }, range: executedRange, sql, parameters };
    setSampleExecution(submitted);
    setQueryGeneration(id);
    pending.current = true;
    setLoading(true); setError(''); setSampleError(''); setResult(null); setSamples(null);
    try {
      const [chart, events] = await Promise.allSettled([
        searchSql({ sql, ...executedRange, parameters, rowLimit: 10000 }),
        sampleSql ? searchSql({ sql: sampleSql, ...executedRange, parameters, rowLimit: 500 }) : Promise.resolve(null),
      ]);
      if (generation.current !== id) return;
      if (chart.status === 'fulfilled') {
        setResult(chart.value);
        setExecution(submitted);
      } else setError(chart.reason instanceof Error ? chart.reason.message : String(chart.reason));
      if (events.status === 'fulfilled') setSamples(events.value);
      else setSampleError(events.reason instanceof Error ? events.reason.message : String(events.reason));
      setRange(executedRange);
    } finally { if (generation.current === id) { setLoading(false); pending.current = false; } }
  }

  useEffect(() => {
    let cancelled = false;
    if (active && workspace.mode !== 'panel' && !workspace.onApply && !initialized.current) queueMicrotask(() => {
      if (cancelled) return;
      initialized.current = true;
      void run();
    });
    return () => { cancelled = true; };
  }, [active]);
  useEffect(() => () => { generation.current++; }, []);
  const display = { ...(execution?.settings ?? settings), format: execution?.series.format ?? settings.format };
  const label = execution ? execution.series.alias.trim() || t('charts.barTotal', { metric: seriesLabel(execution.series) }) : '';
  const data = result ? barData(result, label) : null;
  const option = result ? buildBarOption(result, label, display, theme.border) : null;
  if (!active) return null;

  return <div data-testid="bar-explorer">
    <div className="flex flex-wrap items-center gap-2 border-b border-border/60 py-3">
      <label htmlFor="bar-name" className="text-muted">{t('charts.chartName')}</label>
      <Input id="bar-name" value={name} onChange={(event) => onNameChange(event.target.value)} placeholder={t('charts.chartNamePlaceholder')} maxLength={80} className="min-w-40 flex-1" />
      <Button variant={mode === 'builder' ? 'secondary' : 'ghost'} onClick={() => setMode('builder')}>Builder</Button>
      <Button variant={mode === 'sql' ? 'secondary' : 'ghost'} onClick={() => { setMode('sql'); if (!sqlText) { setSqlText(execution?.sql ?? ''); setSqlParameters(execution?.parameters ?? {}); } }}>SQL</Button>
    </div>
    <div className="flex flex-wrap items-center gap-3 border-b border-border/60 py-3">
      <span className="text-muted">{t('charts.dataSource')}</span>
      <div className="w-48"><Select aria-label={t('charts.dataSource')} value={source} onValueChange={(value) => {
        setSource(value as ChartSource); setGroups([]); setSortField('value'); setWhereDrafts({ lucene: '', sql: '' }); setValueFilters([]); columnDraft.current = value === 'iot' ? 'num_value' : value === 'metrics' ? 'value' : '';
        setSeries({ id: 'bar', aggFunc: value === 'metrics' || value === 'iot' ? 'avg' : 'count', field: value === 'iot' ? 'num_value' : value === 'metrics' ? 'value' : '', where: '', alias: '' });
      }} options={(['logs', 'traces', 'metrics', 'sessions', 'iot'] as const).map((value) => ({ value, label: t(value === 'logs' ? 'search.sourceLogs' : value === 'traces' ? 'search.sourceTraces' : value === 'iot' ? 'charts.sourceMqtt' : value === 'metrics' ? 'charts.sourceMetrics' : 'charts.sourceSessions') }))} /></div>
      <span className="text-xs text-muted">{t('charts.barHint')}</span>
    </div>
    {mode === 'builder' ? <div className="space-y-3 border-b border-border/60 py-3">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <span className="text-muted">A</span><label htmlFor="bar-alias">{t('charts.alias')}</label>
        <div className="w-44"><Input id="bar-alias" value={series.alias} onChange={(event) => setSeries({ ...series, alias: event.target.value })} placeholder={t('charts.seriesAlias')} maxLength={64} /></div>
        <Button variant="link" size="sm" icon={<Palette className="size-3.5" />} onClick={() => setFormatOpen('series')}>{t('charts.seriesFormat')}</Button>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <div className="w-40"><Select aria-label={t('charts.barAggregation')} value={series.aggFunc} onValueChange={(value) => {
          const agg = value as AggFunc;
          if (series.aggFunc !== 'count') columnDraft.current = series.field;
          setSeries({ ...series, aggFunc: agg, field: agg === 'count' ? '' : columnDraft.current });
        }} options={(['count', 'avg', 'p95', 'sum', 'min', 'max', 'count_distinct'] as const).map((value) => ({ value, label: t('charts.' + ({ count: 'aggCount', avg: 'aggAvg', p95: 'aggP95', sum: 'aggSum', min: 'aggMin', max: 'aggMax', count_distinct: 'aggCountDistinct' }[value])) }))} /></div>
        {series.aggFunc !== 'count' && <div className="w-56 min-w-0"><WhereSuggestInput domain={source} syntax="sql" value={series.field} onChange={(field) => { setSeries({ ...series, field }); columnDraft.current = field; }} onRun={() => void run()} testId="bar-column" fieldsOnly ariaLabel={t('charts.barColumn')} placeholder={t('charts.barColumn')} from={null} to={null} denoise={false} /></div>}
        <span className="text-muted">{t('charts.where')}</span>
        <div className="w-28 shrink-0"><Select aria-label={t('charts.eventSyntax')} value={syntax} onValueChange={(value) => {
          const next = value as 'lucene' | 'sql';
          setWhereDrafts({ ...whereDrafts, [syntax]: series.where });
          setSyntax(next);
          setSeries({ ...series, where: whereDrafts[next] });
        }} options={[{ value: 'sql', label: 'SQL' }, { value: 'lucene', label: 'Lucene' }]} /></div>
        <div className="min-w-48 flex-1"><WhereSuggestInput domain={source} syntax={syntax} value={series.where} onChange={(where) => {
          setSeries({ ...series, where });
          setWhereDrafts({ ...whereDrafts, [syntax]: where });
        }} onRun={() => void run()} testId="bar-where" ariaLabel={t('charts.where')} placeholder={t(syntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${source}` : 'search.sqlWherePlaceholder')}
          from={active ? range.from : null} to={active ? range.to : null} denoise={false} /></div>
        <span className="text-muted">{t('charts.groupBy')}</span>
        <div className="w-48"><Select aria-label={t('charts.groupBy')} value="" placeholder={t('charts.groupByPlaceholder')} onValueChange={(field) => setGroups([...groups, field])} options={fields.filter((item) => item.field !== 'value' && !groups.includes(item.field)).map(({ field }) => ({ value: field, label: field }))} /></div>
      </div>
      {syntax === 'lucene' && <LuceneSearchHelp source={source} value={series.where} context="chart" />}
      {groups.length > 0 && <div className="flex flex-wrap items-center gap-2">{groups.map((field) => <Button key={field} size="sm" onClick={() => { setGroups(groups.filter((item) => item !== field)); if (sortField === field) setSortField('value'); }}>{field} ×</Button>)}</div>}
    </div> : <div className="space-y-2 py-3"><p className="text-xs text-muted">{t('charts.barSqlHint')}</p><SqlEditor value={sqlText} onChange={setSqlText} onRun={() => void run()} /></div>}
    <div className="flex flex-wrap items-center justify-between gap-2 py-3">
      {mode === 'builder' ? <div className="flex items-center gap-2"><span>{t('charts.barOrder')}</span>
        <div className="w-40"><Select aria-label={t('charts.barOrder')} value={sortField} onValueChange={setSortField} options={['value', ...groups.filter((field) => field !== 'value')].map((field) => ({ value: field, label: field }))} /></div>
        <div className="w-28"><Select aria-label={t('charts.barDirection')} value={direction} onValueChange={setDirection} options={[{ value: 'DESC', label: 'DESC' }, { value: 'ASC', label: 'ASC' }]} /></div>
      </div> : <span />}
      <Button icon={<Palette className="size-3.5" />} onClick={() => setFormatOpen('chart')}>{t('charts.displaySettings')}</Button>
    </div>
    <div className="flex flex-wrap items-center justify-end gap-2 pb-3">
      {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange testId="bar-time-range" />}
      <Button variant="primary" icon={<Play className="size-3.5" />} disabled={loading} testId="bar-run" onClick={() => void run()}>{loading ? t('common.loading') : t('charts.run')}</Button>
      {writable && <Button icon={<Save className="size-3.5" />} disabled={!result || loading || !!error || !!data?.invalid} onClick={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</Button>}
    </div>
    {valueFilters.length > 0 && <div className="flex flex-wrap gap-2 py-2">{valueFilters.map((filter, index) => <Button key={index} size="sm" disabled={loading} aria-label={t('common.delete') + ' ' + filter.field} onClick={() => {
      const filters = valueFilters.filter((_, i) => i !== index);
      if (sampleExecution) restoreSample(sampleExecution, filters);
    }}>{filter.field} = {String(filter.value ?? '(null)')} ×</Button>)}</div>}
    {error && <p role="alert" className="py-2 text-error">{error}</p>}
    {result?.truncated && <p role="status" className="py-2 text-warning">{t('charts.resultTruncated')}</p>}
    <div className="h-[400px] min-w-0">{loading ? <Empty description={t('common.loading')} className="py-24" /> : data?.invalid ? <p role="alert" className="py-24 text-center text-warning">{t('charts.barValueRequired')}</p> : option ? <Chart option={option} minHeight={400} /> : <Empty description={t('charts.noDataHint')} className="py-24" />}</div>
    <SampleMatchedEvents source={sampleExecution?.source ?? source} range={sampleExecution?.range ?? range} mode={sampleExecution?.editor.mode ?? mode} result={samples} loading={loading} error={sampleError} generation={queryGeneration} onFilter={filterSamples} />
    <button type="button" className="flex w-full items-center justify-between border-t border-border py-3" aria-expanded={sqlOpen} onClick={() => setSqlOpen(!sqlOpen)}>{t('charts.generatedSql')}<Code className="size-3.5" /></button>
    {sqlOpen && execution && <SqlEditor value={displaySql(execution.sql, execution.parameters)} readOnly height="240px" />}
    {formatOpen && <BarDisplayModal settings={formatOpen === 'series' ? { format: series.format ?? settings.format } : settings} seriesOnly={formatOpen === 'series' || mode === 'sql'} onClose={() => setFormatOpen(null)} onApply={(next) => {
      if (formatOpen === 'series') {
        setSeries({ ...series, format: next.format });
        setExecution((current) => current ? { ...current, series: { ...current.series, format: next.format } } : current);
      } else {
        setSettings(mode === 'sql' ? { ...settings, format: next.format } : next);
        setExecution((current) => current ? { ...current, settings: { ...current.settings, format: next.format } } : current);
      }
    }} />}
    {(saveOpen || workspace.onApplyActionChange) && execution && result && !loading && !error && !data?.invalid && <SaveChartModal editor={{ ...execution.editor, series: [execution.series] }} open={saveOpen} onOpenChange={setSaveOpen} name={name} chartType="bar" source={execution.source} series={[execution.series]} range={execution.range} sql={execution.sql} parameters={execution.parameters} barSettings={{ ...execution.settings, format: settings.format }} barGroupBy={execution.groupBy} orderBy={execution.orderBy} display="bar" showLegend autoGranularity={false} />}
  </div>;
}
