import { displaySql } from './displaySql';
import { compileChartWhere } from './chartWhere';
import { ExplorerResultStatus } from './ExplorerResultStatus';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { tableFields } from './tableBuilder';
import { SaveChartModal } from './SaveChartModal';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { useEffect, useRef, useState } from 'react';
import dayjs from 'dayjs';
import { Code, Copy, LoaderCircle, Play, Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import type { ChartSource } from '@/api/chartTypes';
import { Button, Drawer, Input, Modal, Select, TimeRangePicker, TIME_RANGE_PRESETS, toast, type TimeRange } from '@/design-system';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { LuceneSearchHelp } from '@/pages/search/LuceneSearchHelp';
import { LogDetailDrawer } from '@/pages/search/LogDetailDrawer';
import { TraceDetailDrawer } from '@/pages/search/TraceDetailDrawer';
import { buildPatternSql, PATTERN_DEFAULTS, PatternExpressionError } from './patterns';
import { PatternsTable } from './PatternsTable';

interface Execution { editor: import('@/api/chartTypes').ChartEditorDefinition; domain: ChartSource; range: TimeRange; sql: string; parameters: SearchSqlParameters; where: string; expression: string }

/** 模式编辑与执行状态独立于普通图表，详情只读取已执行快照. */
export function PatternsExplorer({ active, name, onNameChange }: { active: boolean; name: string; onNameChange: (value: string) => void }) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const initial = workspace.definition?.chartType === 'patterns' ? workspace.definition : undefined;
  const [domain, setDomain] = useState<ChartSource>(initial?.source ?? 'logs');
  const [expression, setExpression] = useState(initial?.patternExpression ?? '');
  const [where, setWhere] = useState(initial?.where ?? '');
  const [syntax, setSyntax] = useState<'lucene' | 'sql'>(initial?.syntax ?? 'lucene');
  const [range, setRange] = useState<TimeRange>(() => workspace.range ?? (initial ? workspace.urlState?.range : undefined) ?? ({ from: dayjs().subtract(1, 'hour').toISOString(), to: dayjs().toISOString(), preset: '1h' }));
  const [parameters, setParameters] = useState<SearchSqlParameters>(initial ? workspace.urlState?.parameters ?? workspace.initial?.query?.parameters ?? {} : {});
  const [execution, setExecution] = useState<Execution | null>(null);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sample, setSample] = useState<Record<string, unknown> | null>(null);
  const [trace, setTrace] = useState<Record<string, unknown> | null>(null);
  const [sqlOpen, setSqlOpen] = useState(false);
  const writable = canWriteRole(useAuthStore((state) => state.role));
  const [saveOpen, setSaveOpen] = useState(false);
  const sequence = useRef(0);
  const started = useRef(false);
  const [valueFilters, setValueFilters] = useState<{ field: string; value: unknown }[]>(initial?.valueFilters ?? []);
  const sourceDrafts = useRef<Partial<Record<ChartSource, { expression: string; where: string; syntax: 'lucene' | 'sql'; parameters: SearchSqlParameters; filters: { field: string; value: unknown }[] }>>>({});
  useEffect(() => () => { sequence.current++; }, []);

  useChartUrlState(active, {
    editor: { version: 1, chartType: 'patterns', mode: 'builder', source: domain, series: [], formulas: [], groupBy: [], granularity: 'auto', where, syntax, valueFilters, patternExpression: expression },
    range, parameters,
  });

  const run = async (extra?: { field: string; value: unknown; only: boolean; domain?: ChartSource }) => {
    const selectedRange = extra && execution ? { from: execution.range.from, to: execution.range.to } : range;
    const selectedDomain = extra ? extra.domain ?? execution?.domain ?? domain : domain;
    const selectedExpression = extra ? (execution && selectedDomain === execution.domain ? execution.expression : '') : expression;
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === selectedRange.preset);
    const to = preset ? dayjs() : dayjs(selectedRange.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(selectedRange.from);
    let next: Execution;
    try {
      if (!from.isValid() || !to.isValid() || !from.isBefore(to)) throw new Error(t('search.badTimeInput'));
      const sameSource = selectedDomain === execution?.domain;
      const editor = extra && execution ? execution.editor : null;
      const rawWhere = extra ? extra.only || !sameSource ? '' : editor?.where ?? '' : where;
      const selectedSyntax = extra ? sameSource ? editor?.syntax ?? 'sql' : 'sql' : syntax;
      const filters = extra ? [...(extra.only || !sameSource ? [] : editor?.valueFilters ?? []), { field: extra.field, value: extra.value }] : valueFilters;
      const referenced = new Set([...rawWhere.matchAll(/\$([a-z][a-z0-9_]*)\b/g)].map((match) => match[1]));
      const supplied = extra ? sameSource ? execution?.parameters ?? {} : {} : parameters;
      const boundParameters: SearchSqlParameters = selectedSyntax === 'sql' ? Object.fromEntries(Object.entries(supplied).filter(([key]) => referenced.has(key))) : {};
      const compiled = compileChartWhere(selectedDomain, [{ id: 'patterns', aggFunc: 'count', field: '', where: rawWhere, whereSyntax: selectedSyntax, alias: '' }], filters, boundParameters);
      const condition = compiled[0].where;
      next = { editor: { version: 1, mode: 'builder', chartType: 'patterns', source: selectedDomain, series: [], formulas: [], groupBy: [], granularity: 'auto', where: rawWhere, syntax: selectedSyntax, valueFilters: filters, patternExpression: selectedExpression }, domain: selectedDomain, expression: selectedExpression, where: condition, range: { ...selectedRange, from: from.toISOString(), to: to.toISOString() },
        sql: buildPatternSql({ domain: selectedDomain, expression: selectedExpression, where: condition, parameters: boundParameters }), parameters: boundParameters };
    } catch (err) {
      setError(err instanceof PatternExpressionError ? t('charts.patterns.invalidExpression') : err instanceof Error ? err.message : String(err));
      return;
    }
    const id = ++sequence.current;
    if (extra) {
      setDomain(next.domain); setExpression(next.expression); setWhere(next.editor.where ?? ''); setSyntax(next.editor.syntax ?? 'sql'); setValueFilters(next.editor.valueFilters ?? []); setParameters(next.parameters);
    }
    setSaveOpen(false);
    setExecution(next); setRange(next.range); setResult(null); setError(null); setLoading(true); setSample(null); setTrace(null);
    try {
      const response = await searchSql({ sql: next.sql, parameters: next.parameters, from: next.range.from, to: next.range.to, rowLimit: 200 });
      if (id === sequence.current) setResult(response);
    } catch (err) {
      if (id === sequence.current) setError(err instanceof Error ? err.message : String(err));
    } finally {
      if (id === sequence.current) setLoading(false);
    }
  };
  useEffect(() => {
    let cancelled = false;
    if (active && workspace.mode !== 'panel' && !workspace.onApply && !started.current) queueMicrotask(() => { if (!cancelled) { started.current = true; void run(); } });
    return () => { cancelled = true; };
    // 首次打开运行，之后由用户明确提交编辑状态。
  }, [active]);
  const copy = async () => {
    try { await navigator.clipboard.writeText(execution ? displaySql(execution.sql, execution.parameters) : ''); toast.success(t('charts.copied')); }
    catch { toast.error(t('search.copyFailed')); }
  };
  useEffect(() => { if (!active) { setSample(null); setTrace(null); setSqlOpen(false); setSaveOpen(false); } }, [active]);
  const resultDomain = execution?.domain ?? domain;
  function switchSource(next: ChartSource) {
    sourceDrafts.current[domain] = { expression, where, syntax, parameters, filters: valueFilters };
    sequence.current++; setLoading(false); setExecution(null); setResult(null); setError(null); setSample(null); setTrace(null); setSqlOpen(false); setSaveOpen(false);
    const restored = sourceDrafts.current[next];
    setDomain(next); setExpression(restored?.expression ?? ''); setWhere(restored?.where ?? ''); setSyntax(restored?.syntax ?? (next === 'logs' || next === 'traces' ? 'lucene' : 'sql')); setParameters(restored?.parameters ?? {}); setValueFilters(restored?.filters ?? []);
  }
  const changed = Boolean(execution && (where !== execution.editor.where || expression !== execution.expression || syntax !== execution.editor.syntax || JSON.stringify(valueFilters) !== JSON.stringify(execution.editor.valueFilters ?? []) || range.from !== execution.range.from || range.to !== execution.range.to));
  if (!active) return null;
  return <section aria-label={t('charts.patterns.explorer')} className="min-w-0">
    <div className="flex items-center gap-3 border-b border-border/60 py-3">
      <label htmlFor="patterns-name" className="shrink-0 text-muted">{t('charts.chartName')}</label>
      <Input id="patterns-name" value={name} onChange={(event) => onNameChange(event.target.value)} placeholder={t('charts.chartNamePlaceholder')} maxLength={80} />
    </div>
    <div className="flex items-center gap-3 py-3">
      <span className="text-muted">{t('charts.dataSource')}</span>
      <div className="w-48"><Select value={domain} aria-label={t('charts.dataSource')} onValueChange={(value) => switchSource(value as ChartSource)} options={(['logs', 'traces', 'metrics', 'sessions', 'iot'] as const).map((value) => ({ value, label: t(value === 'logs' ? 'search.sourceLogs' : value === 'traces' ? 'search.sourceTraces' : value === 'iot' ? 'charts.sourceMqtt' : value === 'metrics' ? 'charts.sourceMetrics' : 'charts.sourceSessions') }))} /></div>
      <Button size="sm" variant="ghost" icon={<Code className="size-3.5" />} disabled={!execution} onClick={() => setSqlOpen(true)}>{t('charts.previewSql')}</Button>
    </div>
    <div className="flex items-center gap-3 rounded-sm border border-border bg-surface-2 px-3 py-2">
      <label htmlFor="pattern-expression" className="shrink-0">{t('charts.patterns.expression')}</label>
      <Input id="pattern-expression" value={expression} maxLength={512} onChange={(event) => setExpression(event.target.value)} placeholder={t('charts.patterns.expressionPlaceholder', { field: PATTERN_DEFAULTS[domain] })} onKeyDown={(event) => { if (event.key === 'Enter') void run(); }} className="flex-1 font-mono" />
    </div>
    <div className="flex flex-wrap gap-2 py-2">{tableFields(domain).map((item) => <Button key={item.field} variant="link" size="sm" onClick={() => setExpression(item.field)}>{item.field}</Button>)}</div>
    {domain === 'sessions' && <p className="text-xs text-muted">{t('charts.patternSessionScope')}</p>}
    {valueFilters.length > 0 && <div className="flex flex-wrap gap-2 py-2">{valueFilters.map((item, index) => <Button key={index} size="sm" onClick={() => setValueFilters(valueFilters.filter((_, i) => i !== index))}>{item.field}: {String(item.value)} · {t('common.delete')}</Button>)}</div>}
    <div className="my-3 flex gap-2">
      <div className="w-28 shrink-0"><Select value={syntax} aria-label={t('charts.patterns.syntax')} onValueChange={(value) => setSyntax(value as 'lucene' | 'sql')} options={[...(domain === 'logs' || domain === 'traces' ? [{ value: 'lucene', label: 'Lucene' }] : []), { value: 'sql', label: 'SQL' }]} /></div>
      <div className="min-w-0 flex-1"><WhereSuggestInput domain={domain} syntax={syntax} value={where} onChange={setWhere} onRun={() => void run()} testId="patterns-where" ariaLabel={t('charts.where')} from={range.from} to={range.to} denoise={false} placeholder={t(syntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${domain}` : 'charts.patterns.wherePlaceholder')} /></div>
    </div>
    {syntax === 'lucene' && (domain === 'logs' || domain === 'traces') && <LuceneSearchHelp source={domain} value={where} context="chart" />}
    <div className="flex flex-wrap justify-end gap-2 pb-3">
      {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange testId="patterns-time-range" />}
      <Button variant="primary" testId="patterns-run" disabled={loading} onClick={() => void run()} icon={loading ? <LoaderCircle className="size-3.5 animate-spin" /> : <Play className="size-3.5" />}>{t('charts.run')}</Button>
      {writable && <Button icon={<Save className="size-3.5" />} disabled={!result || loading || !!error} onClick={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</Button>}
    </div>
    {execution && <ExplorerResultStatus source={execution.domain} range={execution.range} result={result} changed={changed} />}
    {error && <p role="alert" className="mb-3 rounded-sm border border-error/30 bg-error-soft p-3 text-error">{error}</p>}
    {result?.truncated && <p role="status" className="text-warning">{t('charts.resultTruncated')}</p>}
    <PatternsTable result={result} loading={loading} range={execution?.range ?? range} onSql={() => setSqlOpen(true)} onOpen={setSample} />
    {sqlOpen && execution && <Modal open onOpenChange={setSqlOpen} title={t('charts.previewSql')} width={900} footer={<Button icon={<Copy className="size-3.5" />} onClick={() => void copy()}>{t('charts.copySql')}</Button>}>
      <SqlEditor value={displaySql(execution.sql, execution.parameters)} readOnly height="360px" />
    </Modal>}
    {(saveOpen || workspace.onApplyActionChange) && execution && result && !loading && !error && <SaveChartModal open={saveOpen} onOpenChange={setSaveOpen} name={name} editor={execution.editor} chartType="patterns" source={execution.domain} series={[]} range={execution.range} sql={execution.sql} parameters={execution.parameters} display="line" showLegend={false} autoGranularity={false} />}
    {sample && resultDomain === 'logs' && <LogDetailDrawer domain="logs" row={sample} onClose={() => setSample(null)} onTraceActivate={(id) => setTrace({ trace_id: id })} onFilterByValue={(field, value) => void run({ field, value, only: false })} onFilterOnlyValue={(field, value) => void run({ field, value, only: true })} />}
    {(trace || (sample && resultDomain === 'traces')) && <TraceDetailDrawer row={trace ?? sample!} from={(execution?.range ?? range).from} to={(execution?.range ?? range).to} onClose={() => { setSample(null); setTrace(null); }} onFilterByValue={(field, value, domain) => void run({ field, value, domain, only: false })} onFilterOnlyValue={(field, value, domain) => void run({ field, value, domain, only: true })} />}
    {sample && resultDomain !== 'logs' && resultDomain !== 'traces' && <Drawer open onOpenChange={(open) => { if (!open) setSample(null); }} title={t('charts.patterns.representative')}><pre className="overflow-auto text-xs">{JSON.stringify(sample, null, 2)}</pre></Drawer>}
  </section>;
}
