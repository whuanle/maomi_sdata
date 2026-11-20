import { displaySql } from './displaySql';
import { ExplorerResultStatus } from './ExplorerResultStatus';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { useEffect, useRef, useState } from 'react';
import dayjs from 'dayjs';
import { CircleHelp, Code, Copy, LoaderCircle, MoreHorizontal, Play, Table2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import { Button, Checkbox, DataTable, Drawer, DropdownMenu, Empty, Input, Modal, Popover, Select, TimeRangePicker, TIME_RANGE_PRESETS, toast, type TimeRange } from '@/design-system';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { SelectColumnsPopover, SortEditorPopover } from '@/pages/search/ColumnPicker';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { LuceneSearchHelp } from '@/pages/search/LuceneSearchHelp';
import { ResultsTable } from '@/pages/search/ResultsTable';
import { LogDetailDrawer } from '@/pages/search/LogDetailDrawer';
import { TraceDetailDrawer } from '@/pages/search/TraceDetailDrawer';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { ReplayView } from '@/pages/rum/RumPage';
import { formatDateTime, formatDurationUs } from '@/utils/format';
import { buildEventSearch, EVENT_DEFAULT_FIELDS, eventFields, type EventSearchConfig } from './eventSearchQuery';
import { SaveChartModal } from './SaveChartModal';
import type { ChartSource } from './buildChartSql';

const PAGE_SIZE = 200;
interface Execution {
  generation: number;
  config: EventSearchConfig;
  range: TimeRange;
  built: ReturnType<typeof buildEventSearch>;
  offset: number;
  hasMore: boolean;
}

function initialConfig(domain: ChartSource): EventSearchConfig {
  return { domain, fields: EVENT_DEFAULT_FIELDS[domain], syntax: domain === 'logs' || domain === 'traces' ? 'lucene' : 'sql', where: '', orderBy: 'ts DESC', filters: [] };
}

/** 图表 Search 页签保留独立编辑状态；结果、续页与详情都绑定执行快照. */
export function EventSearchExplorer({ active, name, onNameChange }: { active: boolean; name: string; onNameChange: (value: string) => void }) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const initial = workspace.definition?.chartType === 'search' ? workspace.definition : undefined;
  const writable = canWriteRole(useAuthStore((state) => state.role));
  const [configs, setConfigs] = useState<Record<ChartSource, EventSearchConfig>>(() => Object.fromEntries(
    (['logs', 'traces', 'metrics', 'sessions', 'iot'] as const).map((domain) => [domain, initial?.eventSearch?.domain === domain ? initial.eventSearch : initialConfig(domain)]),
  ) as Record<ChartSource, EventSearchConfig>);
  const [domain, setDomain] = useState<ChartSource>(initial?.source ?? 'logs');
  const config = configs[domain];
  const [range, setRange] = useState<TimeRange>(() => workspace.range ?? (initial ? workspace.urlState?.range : undefined) ?? ({ from: dayjs().subtract(1, 'hour').toISOString(), to: dayjs().toISOString(), preset: '1h' }));
  const [execution, setExecution] = useState<Execution | null>(null);
  const pageRef = useRef<Execution | null>(null);
  const sequence = useRef(0);
  const pending = useRef(false);
  const started = useRef(false);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [moreError, setMoreError] = useState<string | null>(null);
  const [detail, setDetail] = useState<Record<string, unknown> | null>(null);
  const [trace, setTrace] = useState<Record<string, unknown> | null>(null);
  const [sourceDetail, setSourceDetail] = useState<Record<string, unknown> | null>(null);
  const [sqlOpen, setSqlOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const patch = (value: Partial<EventSearchConfig>) => setConfigs((previous) => ({ ...previous, [domain]: { ...previous[domain], ...value } }));
  const supported = domain === 'logs' || domain === 'traces';

  useEffect(() => () => { sequence.current += 1; started.current = false; }, []);

  useChartUrlState(active, {
    editor: { version: 1, chartType: 'search', mode: 'builder', source: domain, series: [], formulas: [], groupBy: [], granularity: 'auto', eventSearch: config },
    range,
  });

  const run = async (next = config, selectedRange = range) => {
    let built: ReturnType<typeof buildEventSearch>;
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === selectedRange.preset);
    const to = preset ? dayjs() : dayjs(selectedRange.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(selectedRange.from);
    try {
      if (!from.isValid() || !to.isValid() || !from.isBefore(to)) throw new Error(t('search.badTimeInput'));
      built = buildEventSearch(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      return;
    }
    const current: Execution = {
      generation: ++sequence.current,
      config: { ...next, filters: [...next.filters] },
      range: { ...selectedRange, from: from.toISOString(), to: to.toISOString() },
      built, offset: 0, hasMore: false,
    };
    pageRef.current = current;
    pending.current = false;
    setSaveOpen(false);
    setExecution(current);
    setRange(current.range);
    setLoading(true); setLoadingMore(false); setResult(null); setError(null); setMoreError(null);
    setDetail(null); setTrace(null); setSourceDetail(null);
    try {
      const response = await searchSql({ sql: built.sql, from: current.range.from, to: current.range.to, rowLimit: PAGE_SIZE,
        parameters: { ...built.parameters, page_limit: PAGE_SIZE + 1, page_offset: 0 } });
      if (current.generation !== sequence.current) return;
      current.offset = response.rows.length;
      current.hasMore = response.truncated && response.rows.length > 0;
      setResult(response);
    } catch (err) {
      if (current.generation === sequence.current) setError(err instanceof Error ? err.message : String(err));
    } finally {
      if (current.generation === sequence.current) setLoading(false);
    }
  };

  useEffect(() => {
    let cancelled = false;
    if (active && workspace.mode !== 'panel' && !workspace.onApply && !started.current) queueMicrotask(() => { if (!cancelled) { started.current = true; void run(); } });
    return () => { cancelled = true; };
    // 首次进入运行，此后由 Run/列头操作明确提交编辑状态。
  }, [active]);

  const loadMore = async () => {
    const current = pageRef.current;
    if (!current || !current.hasMore || pending.current) return;
    pending.current = true; setLoadingMore(true); setMoreError(null);
    try {
      const response = await searchSql({ sql: current.built.sql, from: current.range.from, to: current.range.to, rowLimit: PAGE_SIZE,
        parameters: { ...current.built.parameters, page_limit: PAGE_SIZE + 1, page_offset: current.offset } });
      if (current.generation !== sequence.current) return;
      current.offset += response.rows.length;
      current.hasMore = response.truncated && response.rows.length > 0;
      setResult((previous) => previous && ({ ...previous, rows: [...previous.rows, ...response.rows.map((cells) => previous.columns.map((field) => cells[response.columns.indexOf(field)]))],
        truncated: current.hasMore, elapsedMs: previous.elapsedMs + response.elapsedMs }));
    } catch (err) {
      if (current.generation === sequence.current) setMoreError(err instanceof Error ? err.message : String(err));
    } finally {
      if (current.generation === sequence.current) { pending.current = false; setLoadingMore(false); }
    }
  };

  const commit = (next: EventSearchConfig, selectedRange = range) => {
    setDomain(next.domain);
    setConfigs((previous) => ({ ...previous, [next.domain]: next }));
    void run(next, selectedRange);
  };
  const filter = (field: string, value: unknown, only = false, target: ChartSource = execution?.config.domain ?? domain) => {
    if (!execution) return;
    const base = target === execution.config.domain ? execution.config : configs[target];
    const next = { ...base, filters: [...(only ? base.filters.filter((item) => item.field !== field) : base.filters), { field, value }] };
    commit(next, { from: execution.range.from, to: execution.range.to });
  };
  const rows = result?.rows.map((cells) => Object.fromEntries(result.columns.map((field, index) => [field, cells[index]]))) ?? [];
  const resultDomain = execution?.config.domain ?? domain;
  const resultSupported = resultDomain === 'logs' || resultDomain === 'traces';
  const copy = async () => {
    try { await navigator.clipboard.writeText(execution ? displaySql(execution.built.sql, { ...execution.built.parameters, page_limit: PAGE_SIZE + 1, page_offset: 0 }) : ''); toast.success(t('charts.copied')); }
    catch { toast.error(t('search.copyFailed')); }
  };
  const actions = (source = false) => <DropdownMenu.Root>
    <DropdownMenu.Trigger asChild><Button size="sm" variant="ghost" icon={<MoreHorizontal className="size-3.5" />}>{t('charts.actions')}</Button></DropdownMenu.Trigger>
    <DropdownMenu.Content align={source ? 'start' : 'end'}>
      <DropdownMenu.Item disabled={!execution} icon={<Code className="size-3.5" />} onSelect={() => setSqlOpen(true)}>{t('charts.previewSql')}</DropdownMenu.Item>
      {!source && writable && <DropdownMenu.Item disabled={!result || loading || !!error} icon={<Table2 className="size-3.5" />} onSelect={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</DropdownMenu.Item>}
    </DropdownMenu.Content>
  </DropdownMenu.Root>;

  useEffect(() => { if (!active) { setDetail(null); setTrace(null); setSourceDetail(null); setSqlOpen(false); setSaveOpen(false); } }, [active]);
  const changed = Boolean(execution && (JSON.stringify(config) !== JSON.stringify(execution.config) || range.from !== execution.range.from || range.to !== execution.range.to));
  if (!active) return null;

  return <section aria-label={t('charts.tabSearch')} className="min-w-0">
    <div className="flex items-center gap-3 border-b border-border/60 py-3">
      <label htmlFor="event-chart-name" className="shrink-0 text-muted">{t('charts.chartName')}</label>
      <Input id="event-chart-name" value={name} onChange={(event) => onNameChange(event.target.value)} placeholder={t('charts.chartNamePlaceholder')} maxLength={80} className="flex-1" />
    </div>
    <div className="flex items-center gap-3 py-3">
      <span className="shrink-0 text-muted">{t('charts.dataSource')}</span>
      <div className="w-48"><Select value={domain} onValueChange={(value) => { sequence.current++; pageRef.current = null; pending.current = false; setDomain(value as ChartSource); setExecution(null); setResult(null); setLoading(false); setLoadingMore(false); setError(null); setMoreError(null); setDetail(null); setTrace(null); setSourceDetail(null); setSaveOpen(false); setSqlOpen(false); }} aria-label={t('charts.dataSource')} options={[
        { value: 'logs', label: t('search.sourceLogs') }, { value: 'traces', label: t('search.sourceTraces') },
        { value: 'metrics', label: t('charts.sourceMetrics') }, { value: 'sessions', label: t('charts.sourceSessions') }, { value: 'iot', label: t('charts.sourceMqtt') },
      ]} /></div>{actions(true)}
    </div>
    <div className="pb-2">
      {supported ? <SelectColumnsPopover domain={domain} value={config.fields} onChange={(fields) => patch({ fields })}
        from={execution?.config.domain === domain ? execution.range.from : null} to={execution?.config.domain === domain ? execution.range.to : null} denoise={false} />
        : <Popover.Root><Popover.Trigger asChild><Button className="w-full justify-start font-mono">SELECT {config.fields}</Button></Popover.Trigger>
          <Popover.Content className="w-72"><div className="space-y-2">{eventFields(domain).map((field) => <label key={field} className="flex items-center gap-2">
            <Checkbox checked={config.fields.split(',').map((item) => item.trim()).includes(field)} disabled={field === 'ts'} onChange={(event) => {
              const selected = config.fields.split(',').map((item) => item.trim());
              patch({ fields: (event.target.checked ? [...selected, field] : selected.filter((item) => item !== field)).join(', ') });
            }} />{field}</label>)}</div></Popover.Content></Popover.Root>}
    </div>
    <div className="flex items-center gap-2 pb-3">
      <div className="w-28 shrink-0"><Select value={config.syntax} aria-label={t('charts.eventSyntax')} onValueChange={(value) => patch({ syntax: value as 'lucene' | 'sql' })}
        options={[{ value: 'lucene', label: 'Lucene' }, { value: 'sql', label: 'SQL' }]} /></div>
      <Popover.Root><Popover.Trigger asChild><Button variant="ghost" size="sm" icon={<CircleHelp className="size-3.5" />}>{t('charts.eventSyntaxHelp')}</Button></Popover.Trigger>
        <Popover.Content className="max-w-sm"><p className="text-xs">{t(config.syntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${domain}` : 'search.sqlWherePlaceholder')}</p></Popover.Content></Popover.Root>
      <div className="min-w-0 flex-1"><WhereSuggestInput domain={domain} syntax={config.syntax} value={config.where} onChange={(where) => patch({ where })} onRun={() => void run()}
        testId="chart-event-where" placeholder={t(config.syntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${domain}` : 'search.sqlWherePlaceholder')}
        from={execution?.config.domain === domain ? execution.range.from : null} to={execution?.config.domain === domain ? execution.range.to : null} denoise={false} /></div>
    </div>
    {config.syntax === 'lucene' && <LuceneSearchHelp source={domain} value={config.where} context="chart" />}
    {config.filters.length > 0 && <div className="flex flex-wrap gap-2 pb-2">{config.filters.map((item, index) => <Button key={index} size="sm" variant="secondary" onClick={() => patch({ filters: config.filters.filter((_, i) => i !== index) })}>
      {item.field}: {item.exists ? '*' : String(item.value)} · {t('common.delete')}
    </Button>)}</div>}
    <div className="flex flex-wrap items-center justify-end gap-2 pb-3">
      <div className="mr-auto min-w-48">{supported ? <SortEditorPopover domain={domain} value={config.orderBy} onChange={(orderBy) => patch({ orderBy })} />
        : <Input value={config.orderBy} aria-label="ORDER BY" onChange={(event) => patch({ orderBy: event.target.value })} />}</div>
      {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange align="end" testId="chart-event-range" />}
      <Button variant="primary" testId="chart-event-run" icon={loading ? <LoaderCircle className="size-3.5 animate-spin" /> : <Play className="size-3.5" />} onClick={() => void run()}>{t('charts.run')}</Button>{actions()}
    </div>
    {execution && <ExplorerResultStatus source={execution.config.domain} range={execution.range} result={result} changed={changed} />}
    {error && <p role="alert" className="mb-2 rounded border border-error/30 bg-error-soft px-3 py-2 text-error">{error}</p>}
    {result && <div role="status" className="flex gap-3 pb-1 text-xs text-muted"><span>{t('search.resultsCount', { count: result.rows.length })}{result.truncated ? '+' : ''}</span><span>{t('charts.eventElapsed', { ms: result.elapsedMs })}</span></div>}
    <div className="h-[520px] min-h-80 overflow-hidden border-y border-border/60" data-testid="chart-event-results">
      {resultSupported ? <ResultsTable domain={resultDomain} columns={execution?.built.columns ?? []} rows={rows} loading={loading} queryGeneration={execution?.generation}
        hasMore={result?.truncated} loadingMore={loadingMore} loadMoreError={moreError} onLoadMore={() => void loadMore()} complete={!!result && !result.truncated}
        empty={<Empty description={t(error ? 'charts.eventFailed' : 'charts.noDataHint')} className="py-12" />}
        onTraceOpen={(traceId) => setTrace({ trace_id: traceId })} onRowOpen={(row) => resultDomain === 'traces' ? setTrace(row) : setDetail(row)} onFilterByValue={filter}
        onSort={(field, desc) => execution && commit({ ...execution.config, orderBy: field + (desc ? ' DESC' : ' ASC') }, { from: execution.range.from, to: execution.range.to })}
        onAddFilter={(field) => {
          if (!execution) return;
          setDomain(execution.config.domain);
          setConfigs((previous) => ({ ...previous, [execution.config.domain]: { ...execution.config, filters: [...execution.config.filters, { field, value: null, exists: true }] } }));
        }} />
        : <div className="h-full overflow-auto"><DataTable columns={(execution?.built.columns ?? []).map((field) => ({ key: field, title: field, dataIndex: field, mono: true, ellipsis: true,
          render: (value: unknown) => field === 'ts' ? formatDateTime(String(value)) : field === 'duration_us' ? formatDurationUs(Number(value)) : typeof value === 'object' ? JSON.stringify(value) : String(value ?? '-') }))}
          dataSource={rows} loading={loading} rowKey={(_, index) => String(index)} pagination={false} onRow={(row) => ({ onClick: () => setSourceDetail(row), className: 'cursor-pointer hover:bg-surface-2' })} />
          {moreError && <p role="alert" className="p-2 text-error">{moreError}</p>}
          {result?.truncated ? <Button disabled={loadingMore} onClick={() => void loadMore()}>{t(moreError ? 'search.retry' : 'charts.eventLoadMore')}</Button>
            : result && <p className="p-2 text-center text-muted">{t('charts.eventComplete')}</p>}
        </div>}
    </div>
    {sqlOpen && execution && <Modal open onOpenChange={setSqlOpen} title={t('charts.previewSql')} width={800} footer={<Button icon={<Copy className="size-3.5" />} onClick={() => void copy()}>{t('charts.copySql')}</Button>}>
      <SqlEditor value={displaySql(execution.built.sql, { ...execution.built.parameters, page_limit: PAGE_SIZE + 1, page_offset: 0 })} readOnly height="320px" />    </Modal>}
    {(saveOpen || workspace.onApplyActionChange) && execution && result && !loading && !error && <SaveChartModal editor={{ version: 1, mode: 'builder', chartType: 'search', source: execution.config.domain, eventSearch: execution.config, series: [], formulas: [], groupBy: [], granularity: 'auto' }} open={saveOpen} onOpenChange={setSaveOpen} name={name} sql={execution.built.panelSql} parameters={execution.built.parameters as SearchSqlParameters} chartType="search" source={execution.config.domain} series={[]} range={execution.range} display="line" showLegend={false} autoGranularity={false} />}
    {detail && execution && <LogDetailDrawer domain="logs" row={detail} onClose={() => setDetail(null)} onFilterByValue={filter} onFilterOnlyValue={(field, value) => filter(field, value, true)} onTraceActivate={(traceId) => setTrace({ trace_id: traceId })} />}
    {trace && execution && <TraceDetailDrawer row={trace} from={execution.range.from} to={execution.range.to} onClose={() => setTrace(null)} onFilterByValue={(field, value, target) => filter(field, value, false, target ?? 'traces')} onFilterOnlyValue={(field, value, target) => filter(field, value, true, target ?? 'traces')} />}
    {sourceDetail && execution && <Drawer open onOpenChange={(open) => { if (!open) setSourceDetail(null); }} title={t(resultDomain === 'iot' ? 'charts.sourceMqtt' : resultDomain === 'sessions' ? 'charts.sourceSessions' : 'charts.sourceMetrics')}>
      <pre className="overflow-auto text-xs">{JSON.stringify(sourceDetail, null, 2)}</pre>
      {resultDomain === 'sessions' && <ReplayView projectKey={String(sourceDetail.project_key ?? '')} sessionId={String(sourceDetail.session_id ?? '')} range={execution.range} />}
    </Drawer>}
  </section>;
}
