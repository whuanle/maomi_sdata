import { panelEditorSettings } from './panelDefinition';
import { displaySql } from './displaySql';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { useEffect, useRef, useState } from 'react';
import dayjs from 'dayjs';
import { Code, Copy, Plus, Play, Save, Settings, Trash2, Sigma } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, Drawer, Input, Popconfirm, Select, TimeRangePicker, TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';
import { SEARCH_FIELDS, searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import type { AggFunc, ChartSeries, ChartSource, ChartFormula, TableQuerySettings, TableDisplaySettings, TableColumnIdentity } from '@/api/chartTypes';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { ResultsTable } from '@/pages/search/ResultsTable';
import { LogDetailDrawer } from '@/pages/search/LogDetailDrawer';
import { TraceDetailDrawer } from '@/pages/search/TraceDetailDrawer';
import { buildChartSql, DOMAIN_COLUMNS } from './buildChartSql';
import { bindChartParameter, parseFormula, seriesRef } from './formula';
import { tableColumns } from './tableQuery';
import { ChartTable } from './ChartTable';
import { TableSettings } from './TableSettings';
import { SaveChartModal } from './SaveChartModal';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { TableFilterEditor } from './TableFilterEditor';
import { aggregateFields, compileTableSeries, defaultGroup, defaultTableSeries, tableFields, type TableBuilderSeries } from './tableBuilder';
import { formatDateTime } from '@/utils/format';

interface Draft {
  source: ChartSource; mode: 'builder' | 'sql'; series: TableBuilderSeries[]; formulas: ChartFormula[];
  groups: string[]; query: TableQuerySettings; range: TimeRange; sql: string; parameters: SearchSqlParameters;
}
interface Execution extends Draft { compiledSeries: ChartSeries[]; columns: TableColumnIdentity[]; panelSql: string; panelParameters: SearchSqlParameters }
const initialSeries = (): ChartSeries => ({ id: crypto.randomUUID(), ref: 'A', aggFunc: 'count', field: '', where: '', alias: '' });
const aggregations: AggFunc[] = ['count', 'count_distinct', 'avg', 'sum', 'min', 'max', 'p50', 'p90', 'p95', 'p99'];

/** Table 的草稿、执行快照与时序图隔离；Dashboard 复用相同查询和渲染契约. */
export function TableExplorer({ active, name, onNameChange }: { active: boolean; name: string; onNameChange: (value: string) => void }) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const saved = panelEditorSettings(workspace.initial);
  const initial = workspace.definition?.chartType === 'table' ? workspace.definition : undefined;
  const writable = canWriteRole(useAuthStore((state) => state.role));
  const [source, setSource] = useState<ChartSource>(initial?.source ?? 'logs');
  const [mode, setMode] = useState<'builder' | 'sql'>(initial?.mode ?? 'builder');
  const [series, setSeries] = useState<TableBuilderSeries[]>(() => initial?.tableSeries?.length ? initial.tableSeries : initial?.series.length ? initial.series : [defaultTableSeries(initial?.source ?? 'logs')]);
  const [formulas, setFormulas] = useState<ChartFormula[]>(initial?.formulas ?? []);
  const [group, setGroup] = useState(initial?.groupBy.join(', ') ?? defaultGroup('logs'));
  const [query, setQuery] = useState<TableQuerySettings>(initial?.tableQuery ?? saved?.tableQuery ?? { topN: 10, sort: [] });
  const [sql, setSql] = useState(initial?.mode === 'sql' ? workspace.urlState?.sql ?? workspace.initial?.query?.sql ?? workspace.initial?.sql ?? '' : '');
  const [parameters, setParameters] = useState<SearchSqlParameters>(initial ? workspace.urlState?.parameters ?? workspace.initial?.query?.parameters ?? {} : {});
  const [range, setRange] = useState<TimeRange>(() => workspace.range ?? (initial ? workspace.urlState?.range : undefined) ?? ({ from: dayjs().subtract(1, 'day').toISOString(), to: dayjs().toISOString(), preset: '1d' }));
  const [settings, setSettings] = useState<TableDisplaySettings>(saved?.tableSettings ?? {});
  const [execution, setExecution] = useState<Execution | null>(null);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [samples, setSamples] = useState<AnalyticsQueryResponse | null>(null);
  const [sampleError, setSampleError] = useState('');
  const [error, setError] = useState('');
  const [limited, setLimited] = useState(false);
  const [loading, setLoading] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const [detail, setDetail] = useState<Record<string, unknown> | null>(null);
  const [trace, setTrace] = useState<Record<string, unknown> | null>(null);
  const generation = useRef(0);
  const sampleGeneration = useRef(0);
  const sampleContext = useRef<{ series: ChartSeries[]; parameters: SearchSqlParameters } | null>(null);
  const initialized = useRef(false);
  const nextRef = useRef(Math.max(1, ...(initial?.series ?? []).map((item) => { let n = 0; for (const ch of item.ref ?? '') n = n * 26 + ch.charCodeAt(0) - 64; return n; })));
  const sqlDrafts = useRef<Partial<Record<ChartSource, { sql: string; parameters: SearchSqlParameters }>>>({});
  const groups = group.split(',').map((value) => value.trim()).filter(Boolean);
  const identities = tableColumns({ seriesList: series, formulas, groupBy: groups });
  const draft = (): Draft => ({ source, mode, series, formulas, groups, query, range, sql, parameters });
  const update = (index: number, patch: Partial<TableBuilderSeries>) => setSeries((items) => items.map((item, i) => i === index ? { ...item, ...patch } : item));

  useChartUrlState(active, {
    editor: { version: 1, chartType: 'table', mode, source, series, tableSeries: series, formulas, groupBy: groups, granularity: 'auto', tableQuery: query },
    range, sql, parameters,
  });

  async function run(input: Draft = draft()) {
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === input.range.preset);
    const to = preset ? dayjs() : dayjs(input.range.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(input.range.from);
    if (!from.isValid() || !to.isValid() || !from.isBefore(to)) { setError(t('search.badTimeInput')); return; }
    const fixedRange = { from: from.toISOString(), to: to.toISOString(), preset: input.range.preset };
    const params = input.mode === 'sql' ? { ...input.parameters } : {};
    let compiledSeries: ChartSeries[] = input.series;
    try { if (input.mode === 'builder') compiledSeries = compileTableSeries(input.source, input.series, params); }
    catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
    const build = { domain: input.source, chartType: 'table' as const, seriesList: compiledSeries, formulas: input.formulas, groupBy: input.groups, tableQuery: input.query, granularity: 'auto' as const, rangeMs: to.diff(from), parameters: params };
    let built: string;
    let panelSql: string;
    const panelParameters = { ...params };
    try {
      built = input.mode === 'sql' ? input.sql : buildChartSql(build);
      panelSql = input.mode === 'sql' ? built : buildChartSql({ ...build, parameters: panelParameters, tableProbe: false });
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); return; }
    const id = ++generation.current;
    sampleGeneration.current++;
    sampleContext.current = null;
    const executedQuery = input.mode === 'builder' && !input.query.sort.length ? { ...input.query, sort: [{ columnId: input.series[0].id, direction: 'desc' as const }] } : input.query;
    const current = { ...input, compiledSeries, query: executedQuery, range: fixedRange, sql: built, parameters: params, panelSql, panelParameters, columns: input.mode === 'builder' ? tableColumns(build) : [] };
    setExecution(current); setRange(fixedRange); setLoading(true); setError(''); setResult(null); setLimited(false); setSamples(null); setSampleError(''); setDetail(null); setTrace(null);
    try {
      const response = await searchSql({ sql: built, ...fixedRange, parameters: params, rowLimit: input.mode === 'builder' ? input.query.topN + 1 : 500 });
      if (id !== generation.current) return;
      setLimited(input.mode === 'builder' && response.rows.length > input.query.topN);
      setResult(input.mode === 'builder' ? { ...response, rows: response.rows.slice(0, input.query.topN) } : response);
    } catch (err) { if (id === generation.current) setError(err instanceof Error ? err.message : String(err)); }
    finally { if (id === generation.current) setLoading(false); }
  }

  async function filterSample(field: string, value: unknown, only = false) {
    if (!execution || !DOMAIN_COLUMNS[execution.source].includes(field)) { setSampleError(t('charts.tableUnknownField')); return; }
    const params: SearchSqlParameters = only ? {} : { ...(sampleContext.current?.parameters ?? execution.parameters) };
    const condition = value == null ? '"' + field + '" IS NULL' : '"' + field + '" = ' + bindChartParameter(params, typeof value === 'number' ? value : String(value));
    const items = (only ? execution.series.map((item) => ({ ...item, where: '' })) : sampleContext.current?.series ?? execution.compiledSeries).map((item) => ({ ...item, where: [only ? '' : item.where, condition].filter(Boolean).map((part) => '(' + part + ')').join(' AND ') }));
    const id = generation.current;
    const sampleId = ++sampleGeneration.current;
    try {
      const sampleSql = buildChartSql({ domain: execution.source, chartType: 'search', seriesList: items, groupBy: [], granularity: 'auto', rangeMs: 0 });
      const response = await searchSql({ sql: sampleSql, ...execution.range, parameters: params, rowLimit: 500 });
      if (id === generation.current && sampleId === sampleGeneration.current) { sampleContext.current = { series: items, parameters: params }; setSamples(response); setSampleError(''); setDetail(null); }
    } catch (err) { if (id === generation.current && sampleId === sampleGeneration.current) setSampleError(err instanceof Error ? err.message : String(err)); }
  }

  async function drill(cells: unknown[]) {
    if (!execution || execution.mode !== 'builder' || !execution.groups.length) return;
    const id = generation.current;
    const sampleId = ++sampleGeneration.current;
    const params: SearchSqlParameters = { ...execution.parameters };
    try {
      const filters = execution.groups.map((field) => {
        if (!DOMAIN_COLUMNS[execution.source].includes(field)) throw new Error(t('charts.tableUnknownField'));
        const value = cells[result!.columns.indexOf(field)];
        return value == null ? '"' + field + '" IS NULL' : '"' + field + '" = ' + bindChartParameter(params, typeof value === 'number' ? value : String(value));
      });
      const filtered = execution.compiledSeries.map((item) => ({ ...item, where: [item.where, ...filters].filter(Boolean).map((item) => '(' + item + ')').join(' AND ') }));
      const sampleSql = buildChartSql({ domain: execution.source, chartType: 'search', seriesList: filtered, groupBy: [], granularity: 'auto', rangeMs: 0 });
      const response = await searchSql({ sql: sampleSql, ...execution.range, parameters: params, rowLimit: 500 });
      if (id === generation.current && sampleId === sampleGeneration.current) { sampleContext.current = { series: filtered, parameters: params }; setSamples(response); setSampleError(''); }
    } catch (err) { if (id === generation.current && sampleId === sampleGeneration.current) setSampleError(err instanceof Error ? err.message : String(err)); }
  }

  useEffect(() => {
    let cancelled = false;
    if (active && workspace.mode !== 'panel' && !workspace.onApply && !initialized.current) queueMicrotask(() => { if (!cancelled) { initialized.current = true; void run(); } });
    return () => { cancelled = true; };
  }, [active]);
  useEffect(() => () => { generation.current++; sampleGeneration.current++; }, []);
  function switchSource(next: ChartSource) {
    if (sql.trim()) sqlDrafts.current[source] = { sql, parameters };
    else if (execution?.source === source) sqlDrafts.current[source] = { sql: execution.panelSql, parameters: execution.panelParameters };
    generation.current++; sampleGeneration.current++; sampleContext.current = null;
    setResult(null); setExecution(null); setSamples(null); setError(''); setSampleError(''); setLimited(false); setDetail(null); setTrace(null); setSaveOpen(false);
    const nextSeries = [defaultTableSeries(next)];
    const nextGroups = [defaultGroup(next)];
    const nextQuery = { topN: 10, sort: [] };
    const nextParameters: SearchSqlParameters = {};
    const built = buildChartSql({ domain: next, chartType: 'table', seriesList: nextSeries, groupBy: nextGroups, tableQuery: nextQuery, granularity: 'auto', rangeMs: 86400000, parameters: nextParameters, tableProbe: false });
    const saved = sqlDrafts.current[next] ?? { sql: built, parameters: nextParameters };
    setSource(next); setSeries(nextSeries); setGroup(nextGroups.join(', ')); setQuery(nextQuery); setFormulas([]); setSql(saved.sql); setParameters(saved.parameters); nextRef.current = 1;
    void run({ source: next, mode, series: nextSeries, formulas: [], groups: nextGroups, query: nextQuery, range, sql: saved.sql, parameters: saved.parameters });
  }
  function preset(kind: string) {
    const item = defaultTableSeries(source);
    let dimension = defaultGroup(source);
    if (kind === 'errors') item.filters = [{ id: crypto.randomUUID(), field: 'severity', operator: 'eq', value: 'ERROR' }];
    if (kind === 'severity') dimension = 'severity';
    if (kind === 'latency') { dimension = 'name'; item.aggFunc = 'p95'; item.field = 'duration_us'; }
    setMode('builder'); setSeries([item]); setFormulas([]); setGroup(dimension); setQuery({ topN: 10, sort: [] }); nextRef.current = 1;
    void run({ ...draft(), mode: 'builder', series: [item], formulas: [], groups: [dimension], query: { topN: 10, sort: [] } });
  }
  const changed = execution && (source !== execution.source || mode !== execution.mode || JSON.stringify(series) !== JSON.stringify(execution.series) || JSON.stringify(formulas) !== JSON.stringify(execution.formulas) || groups.join(',') !== execution.groups.join(',') || query.topN !== execution.query.topN || JSON.stringify(query.sort.length ? query.sort : [{ columnId: series[0].id, direction: 'desc' }]) !== JSON.stringify(execution.query.sort) || range.from !== execution.range.from || range.to !== execution.range.to || (mode === 'sql' && sql !== execution.sql));
  if (!active) return null;
  return <section data-testid="table-explorer" className="space-y-3">
    <div className="flex flex-wrap items-center gap-2 border-b border-border py-3">
      <label htmlFor="table-name" className="text-muted">{t('charts.chartName')}</label>
      <Input id="table-name" className="min-w-40 flex-1" value={name} maxLength={80} onChange={(event) => onNameChange(event.target.value)} />
      <Button variant={mode === 'builder' ? 'secondary' : 'ghost'} onClick={() => setMode('builder')}>Builder</Button>
      <Button variant={mode === 'sql' ? 'secondary' : 'ghost'} icon={<Code className="size-3.5" />} onClick={() => { setMode('sql'); if (!sql && execution) { setSql(execution.panelSql); setParameters(execution.panelParameters); } }}>SQL</Button>
    </div>
    <div className="flex flex-wrap items-center gap-3">
      <span className="text-muted">{t('charts.dataSource')}</span>
      <Select className="w-48" aria-label={t('charts.dataSource')} value={source} options={(['logs', 'traces', 'metrics', 'sessions', 'iot'] as const).map((value) => ({ value, label: t(value === 'iot' ? 'charts.sourceMqtt' : value === 'logs' || value === 'traces' ? 'search.source' + (value === 'logs' ? 'Logs' : 'Traces') : 'charts.source' + (value === 'metrics' ? 'Metrics' : 'Sessions')) }))} onValueChange={(value) => switchSource(value as ChartSource)} />
      <span className="text-xs text-muted">{t('charts.tableDescription')}</span>
      <Button size="sm" variant="secondary" onClick={() => preset('default')}>{t('charts.tablePresetDefault')}</Button>
      {source === 'logs' && <><Button size="sm" variant="ghost" onClick={() => preset('errors')}>{t('charts.tablePresetErrors')}</Button><Button size="sm" variant="ghost" onClick={() => preset('severity')}>{t('charts.tablePresetSeverity')}</Button></>}
      {source === 'traces' && <Button size="sm" variant="ghost" onClick={() => preset('latency')}>{t('charts.tablePresetLatency')}</Button>}
    </div>
    {mode === 'builder' ? <>
      <div className="divide-y divide-border rounded border border-border">
        {series.map((item, index) => <div key={item.id} className="space-y-2 p-3">
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded bg-surface-2 px-2 py-1 text-primary">{item.ref}</span>
            <Select className="w-40" aria-label={t('charts.tableAggregation', { index: index + 1 })} value={item.aggFunc} onValueChange={(value) => { const fields = aggregateFields(source, value as AggFunc); update(index, { aggFunc: value as AggFunc, field: fields.some((field) => field.field === item.field) ? item.field : fields[0]?.field ?? '' }); }} options={aggregations.map((value) => ({ value, label: value.startsWith('p') ? value : value + '()', disabled: !['count', 'count_distinct'].includes(value) && !aggregateFields(source, value).length }))} />
            {item.aggFunc !== 'count' && <Select className="w-48" aria-label={t('charts.tableField', { index: index + 1 })} value={item.field} options={aggregateFields(source, item.aggFunc).map((field) => ({ value: field.field, label: field.field + ' · ' + field.kind }))} onValueChange={(field) => update(index, { field })} />}
            <Input className="min-w-40 flex-1" aria-label={t('charts.alias') + ' ' + (index + 1)} value={item.alias} placeholder={t('charts.seriesAlias')} maxLength={64} onChange={(event) => update(index, { alias: event.target.value })} />
            <Button variant="link" size="sm" disabled={series.length >= 12} icon={<Copy className="size-3.5" />} onClick={() => setSeries([...series, { ...item, id: crypto.randomUUID(), ref: seriesRef(nextRef.current++) }])}>{t('charts.duplicateSeries')}</Button>
            {series.length > 1 && <Popconfirm title={t('charts.removeSeries')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => { setSeries(series.filter((other) => other.id !== item.id)); setQuery({ ...query, sort: query.sort.filter((sort) => sort.columnId !== item.id) }); }}><Button variant="link" size="sm" danger icon={<Trash2 className="size-3.5" />}>{t('common.delete')}</Button></Popconfirm>}
          </div>
          {(item.filters ?? []).map((filter) => <TableFilterEditor key={source + filter.id} source={source} value={filter} from={range.from} to={range.to} onChange={(next) => update(index, { filters: item.filters!.map((value) => value.id === next.id ? next : value) })} onRemove={() => update(index, { filters: item.filters!.filter((value) => value.id !== filter.id) })} />)}
          <Button size="sm" variant="link" icon={<Plus className="size-3.5" />} onClick={() => update(index, { filters: [...(item.filters ?? []), { id: crypto.randomUUID(), field: tableFields(source)[0].field, operator: 'eq', value: '' }] })}>{t('charts.tableAddFilter')}</Button>
          <details><summary className="cursor-pointer text-xs text-muted">{t('charts.tableAdvanced')}</summary>
            <div className="mt-2 space-y-2">
              <WhereSuggestInput key={source + item.id} domain={source} syntax="sql" value={item.where} onChange={(where) => update(index, { where })} onRun={() => void run()} testId={'table-where-' + item.id} ariaLabel={'WHERE ' + (index + 1)} from={range.from} to={range.to} denoise={false} placeholder={t('charts.wherePlaceholder')} />
              <div className="flex flex-wrap items-center gap-2"><span>{t('charts.tableThreshold')}</span><Select className="w-24" value={item.threshold?.operator ?? 'gt'} onValueChange={(operator) => update(index, { threshold: { value: item.threshold?.value ?? '', operator: operator as 'gt' } })} options={['gt', 'gte', 'lt', 'lte'].map((value) => ({ value, label: t('charts.tableOp_' + value) }))} /><Input className="w-36" type="number" aria-label={t('charts.tableThreshold') + ' ' + (index + 1)} value={item.threshold?.value ?? ''} placeholder="100" onChange={(event) => update(index, { threshold: { operator: item.threshold?.operator ?? 'gt', value: event.target.value } })} /></div>
              <label className="flex items-center gap-2 text-muted">HAVING<Input aria-label={'HAVING ' + (index + 1)} value={item.having ?? ''} placeholder={t('charts.tableHavingHint')} onChange={(event) => update(index, { having: event.target.value })} /></label>
            </div>
          </details>
        </div>)}
      </div>
      {formulas.map((formula, index) => <div key={formula.id} className="flex flex-wrap items-center gap-2">
        <Sigma className="size-4 text-muted" /><Input aria-label={t('charts.formulaExpression') + ' ' + (index + 1)} className="min-w-40 flex-1" value={formula.expression} placeholder="A / B * 100" onChange={(event) => setFormulas(formulas.map((item) => item.id === formula.id ? { ...item, expression: event.target.value } : item))} />
        <Input className="w-40" aria-label={t('charts.formulaAlias') + ' ' + (index + 1)} value={formula.alias} placeholder={t('charts.seriesAlias')} onChange={(event) => setFormulas(formulas.map((item) => item.id === formula.id ? { ...item, alias: event.target.value } : item))} />
        <Popconfirm title={t('charts.removeFormula')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => { setFormulas(formulas.filter((item) => item.id !== formula.id)); setQuery({ ...query, sort: query.sort.filter((sort) => sort.columnId !== formula.id) }); }}><Button variant="link" danger icon={<Trash2 className="size-3.5" />}>{t('common.delete')}</Button></Popconfirm>
        <div className="flex flex-wrap items-center gap-1 text-xs">{series.map((item) => <Button key={item.id} size="sm" variant="link" onClick={() => setFormulas(formulas.map((value) => value.id === formula.id ? { ...value, expression: value.expression + item.ref } : value))}>{item.ref} · {item.alias || item.aggFunc}</Button>)}{series.length > 1 && <Button size="sm" variant="link" onClick={() => setFormulas(formulas.map((value) => value.id === formula.id ? { ...value, expression: series[0].ref + ' / ' + series[1].ref + ' * 100' } : value))}>{t('charts.tableRatioTemplate')}</Button>}</div>
        {(() => { try { parseFormula(formula.expression, series.map((item) => item.ref!)); return null; } catch { return <p role="alert" className="text-xs text-error">{t('charts.tableFormulaHint')}</p>; } })()}
      </div>)}
      <div className="flex gap-2"><Button variant="ghost" disabled={series.length >= 12} icon={<Plus className="size-3.5" />} onClick={() => setSeries([...series, { ...initialSeries(), ref: seriesRef(nextRef.current++) }])}>{t('charts.tableAddMetric')}</Button><Button variant="ghost" disabled={formulas.length >= 8} icon={<Sigma className="size-3.5" />} onClick={() => setFormulas([...formulas, { id: crypto.randomUUID(), expression: '', alias: '' }])}>{t('charts.addFormula')}</Button></div>
      <div className="flex flex-wrap items-center gap-3 border-y border-border py-3">
        <span className="text-muted">{t('charts.groupBy')}</span>
        {groups.map((field) => <Button key={field} size="sm" variant="secondary" onClick={() => { setGroup(groups.filter((value) => value !== field).join(', ')); setQuery({ ...query, sort: query.sort.filter((sort) => sort.columnId !== 'group:' + field) }); }}>{field} ×</Button>)}
        <Select className="w-52" aria-label={t('charts.groupBy')} value="" placeholder={t('charts.tableAddGroup')} options={tableFields(source).filter((field) => !groups.includes(field.field)).map((field) => ({ value: field.field, label: field.field + ' · ' + field.kind }))} onValueChange={(field) => setGroup([...groups, field].join(', '))} />
        <label className="flex items-center gap-2 text-muted">Top N<Input className="w-24" aria-label="Top N" type="number" min={1} max={500} value={query.topN} onChange={(event) => setQuery({ ...query, topN: Number(event.target.value) })} /></label>
      </div>
      <div className="space-y-2">{query.sort.map((sort, index) => <div key={index} className="flex flex-wrap items-center gap-2">
        <span className="text-muted">{t('charts.tableOrder')}</span><Select className="w-52" aria-label={t('charts.tableOrder') + ' ' + (index + 1)} value={sort.columnId} options={identities.map((column) => ({ value: column.id, label: column.name }))} onValueChange={(columnId) => setQuery({ ...query, sort: query.sort.map((item, i) => i === index ? { ...item, columnId } : item) })} />
        <Select className="w-32" aria-label={t('charts.tableDirection') + ' ' + (index + 1)} value={sort.direction} options={[{ value: 'desc', label: t('search.sortDesc') }, { value: 'asc', label: t('search.sortAsc') }]} onValueChange={(direction) => setQuery({ ...query, sort: query.sort.map((item, i) => i === index ? { ...item, direction: direction as 'asc' | 'desc' } : item) })} />
        <Button variant="link" size="sm" icon={<Trash2 className="size-3.5" />} onClick={() => setQuery({ ...query, sort: query.sort.filter((_, i) => i !== index) })}>{t('common.delete')}</Button>
      </div>)}<Button variant="link" size="sm" disabled={query.sort.length >= identities.length} icon={<Plus className="size-3.5" />} onClick={() => setQuery({ ...query, sort: [...query.sort, { columnId: identities.find((item) => !query.sort.some((sort) => sort.columnId === item.id))!.id, direction: 'desc' }] })}>{t('charts.tableAddSort')}</Button><span className="ml-2 text-xs text-muted">{t('charts.tableDefaultSort')}</span></div>
    </> : <><p className="text-xs text-muted">{t('charts.tableRawHint')}</p><Button variant="link" size="sm" icon={<Code className="size-3.5" />} onClick={() => { const params: SearchSqlParameters = {}; const text = buildChartSql({ domain: source, chartType: 'table', seriesList: [defaultTableSeries(source)], groupBy: [defaultGroup(source)], tableQuery: { topN: 10, sort: [] }, granularity: 'auto', rangeMs: 86400000, parameters: params, tableProbe: false }); setSql(text); setParameters(params); }}>{t('charts.tableSqlExample')}</Button><SqlEditor schema={{ [source]: SEARCH_FIELDS[source].map((field) => field.field) }} value={sql} onChange={setSql} onRun={() => void run()} /></>}
    <div className="flex flex-wrap items-center justify-end gap-2">
      <Button icon={<Settings className="size-3.5" />} onClick={() => setSettingsOpen(true)}>{t('charts.displaySettings')}</Button>
      {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange testId="table-time-range" />}
      <Button variant="primary" testId="table-run" disabled={loading} icon={<Play className="size-3.5" />} onClick={() => void run()}>{t(loading ? 'common.loading' : 'charts.run')}</Button>
      {writable && <Button icon={<Save className="size-3.5" />} disabled={!result || loading || !!error} onClick={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</Button>}
    </div>
    {changed && <p role="status" className="text-xs text-warning">{t('charts.tableDraftChanged')}</p>}
    {execution && <p className="text-xs text-muted">{t('charts.tableExecuted')}: {execution.source} · {formatDateTime(execution.range.from)} – {formatDateTime(execution.range.to)} · {execution.groups.join(', ') || t('charts.tableOverall')}</p>}
    {!loading && result && !result.rows.length && <div className="flex flex-wrap items-center gap-2 rounded border border-border p-3"><span>{t('charts.tableEmptyHint')}</span><Button size="sm" onClick={() => { const next = { from: dayjs().subtract(7, 'day').toISOString(), to: dayjs().toISOString(), preset: '7d' }; setRange(next); void run({ ...draft(), range: next }); }}>{t('charts.tableExpandRange')}</Button><Button size="sm" onClick={() => preset('default')}>{t('charts.tableClearFilters')}</Button></div>}
    {error && <p role="alert" className="rounded border border-error/30 bg-error-soft p-3 text-error">{error}</p>}
    {limited && <p role="status" className="text-xs text-muted">{t('charts.tableTopLimited', { count: execution?.query.topN })}</p>}
    {result?.truncated && <p role="status" className="text-xs text-warning">{t('charts.resultTruncated')}</p>}
    <div className="h-[420px] min-w-0"><ChartTable result={result} settings={settings} columns={execution?.columns} querySort={execution?.query.sort} loading={loading} onRowClick={execution?.mode === 'builder' && execution.groups.length ? (cells) => void drill(cells) : undefined} onSort={execution?.mode === 'builder' ? (sort) => { const next = { ...execution.query, sort: [sort] }; setSource(execution.source); setMode(execution.mode); setSeries(execution.series); setFormulas(execution.formulas); setGroup(execution.groups.join(', ')); setQuery(next); void run({ ...execution, query: next, range: { from: execution.range.from, to: execution.range.to } }); } : undefined} /></div>
    {sampleError && <p role="alert" className="text-error">{sampleError}</p>}
    {samples && execution && <details open><summary className="cursor-pointer py-2 text-muted">{t('charts.sampleEvents')}</summary>{execution.source === 'logs' || execution.source === 'traces' ? <ResultsTable domain={execution.source} columns={samples.columns} rows={samples.rows.map((row) => Object.fromEntries(samples.columns.map((column, i) => [column, row[i]])))} onRowOpen={(row) => execution.source === 'traces' ? setTrace(row) : setDetail(row)} onTraceOpen={(traceId) => setTrace({ trace_id: traceId })} onFilterByValue={(field, value) => void filterSample(field, value)} /> : <div className="h-80"><ChartTable result={samples} settings={{ rowClick: false }} /></div>}</details>}
    {execution && <details><summary className="cursor-pointer py-2 text-muted">{t('charts.generatedSql')}</summary><SqlEditor value={displaySql(execution.sql, execution.parameters)} readOnly height="240px" /></details>}
    {detail && <LogDetailDrawer domain="logs" row={detail} onClose={() => setDetail(null)} onFilterByValue={(field, value) => void filterSample(field, value)} onFilterOnlyValue={(field, value) => void filterSample(field, value, true)} onTraceActivate={(traceId) => setTrace({ trace_id: traceId })} />}
    {trace && execution && <TraceDetailDrawer row={trace} from={execution.range.from} to={execution.range.to} onClose={() => setTrace(null)} onFilterByValue={(field, value) => void filterSample(field, value)} onFilterOnlyValue={(field, value) => void filterSample(field, value, true)} />}
    <Drawer open={settingsOpen} onOpenChange={setSettingsOpen} title={t('charts.displaySettings')}><TableSettings value={{ ...settings, columns: Object.fromEntries(Object.entries(settings.columns ?? {}).map(([id, value]) => [execution?.columns.find((item) => item.id === id)?.name ?? id, value])) }} columns={result?.columns ?? []} onChange={(next) => setSettings({ ...next, columns: Object.fromEntries(Object.entries(next.columns ?? {}).map(([name, value]) => [execution?.columns.find((item) => item.name === name)?.id ?? name, value])) })} /></Drawer>
    {(saveOpen || workspace.onApplyActionChange) && result && execution && !loading && !error && <SaveChartModal editor={{ version: 1, mode: execution.mode, source: execution.source, chartType: 'table', series: execution.series, tableSeries: execution.series, formulas: execution.formulas, groupBy: execution.groups, granularity: 'auto', tableQuery: execution.query }} open={saveOpen} onOpenChange={setSaveOpen} name={name} chartType="table" source={execution.source} series={execution.compiledSeries} formulas={execution.formulas} range={execution.range} sql={execution.panelSql} parameters={execution.panelParameters} tableSettings={settings} tableGroupBy={execution.groups} tableQuery={execution.mode === 'builder' ? execution.query : undefined} tableColumns={execution.columns} display="line" showLegend={false} autoGranularity={false} />}
  </section>;
}
