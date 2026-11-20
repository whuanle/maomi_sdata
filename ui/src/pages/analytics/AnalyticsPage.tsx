import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { Database, PanelLeft, Play, Plus, RotateCw, Save, Trash2, Upload } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { EditorView } from '@uiw/react-codemirror';
import { deleteSavedQuery, listAnalyticsTables, listSavedQueries, saveQuery } from '@/api/analytics';
import { searchSql, SEARCH_FIELDS } from '@/api/search';
import type { ChartSource } from '@/api/chartTypes';
import type { AnalyticsQueryResponse, AnalyticsTableItem, SavedQueryItem } from '@/api/types';
import {
  AuditCell, Button, Field, Input, InputNumber, Modal, Page, Popconfirm, Select,
  Textarea, TimeRangePicker, toast, type TimeRange,
} from '@/design-system';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { formatDateTime } from '@/utils/format';
import { cn } from '@/utils/cn';
import { SqlEditor } from '../search/SqlEditor';
import { WhereSuggestInput } from '../search/WhereSuggestInput';
import { SOURCES, luceneQuery, parseParameters, parseWorkbench, quoteIdentifier } from './workbench';

export function AnalyticsPage() {
  const { t } = useTranslation();
  const canWrite = useAuthStore((state) => canWriteRole(state.role));
  const [mode, setMode] = useState<'sql' | 'lucene'>('sql');
  const [source, setSource] = useState<ChartSource>('logs');
  const [sql, setSql] = useState('SELECT * FROM {{logs}}\nWHERE ts >= $start AND ts < $end\nORDER BY ts DESC');
  const [lucene, setLucene] = useState('');
  const [parameters, setParameters] = useState('{}');
  const [rowLimit, setRowLimit] = useState(1000);
  const [range, setRange] = useState<TimeRange>(() => ({
    from: new Date(Date.now() - 3600000).toISOString(), to: new Date().toISOString(), preset: '1h',
  }));
  const [tables, setTables] = useState<AnalyticsTableItem[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [fieldFilter, setFieldFilter] = useState('');
  const [catalogError, setCatalogError] = useState('');
  const [catalogLoading, setCatalogLoading] = useState(false);
  const [catalogOpen, setCatalogOpen] = useState(true);
  const [saved, setSaved] = useState<SavedQueryItem[]>([]);
  const [savedError, setSavedError] = useState('');
  const [savedOpen, setSavedOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [queryError, setQueryError] = useState('');
  const [executed, setExecuted] = useState('');
  const [running, setRunning] = useState(false);
  const [preview, setPreview] = useState(false);
  const [example, setExample] = useState('');
  const viewRef = useRef<EditorView | null>(null);
  const requestId = useRef(0);
  const catalogId = useRef(0);
  const { register, reset, handleSubmit, formState: { errors, isSubmitting } } = useForm<{ name: string; description: string }>();
  const message = (error: unknown) => {
    const value = error instanceof Error ? error.message : 'analytics.queryFailed';
    return value.startsWith('analytics.') ? t(value) : value;
  };

  const loadCatalog = useCallback(async () => {
    const id = ++catalogId.current;
    setCatalogLoading(true);
    setCatalogError('');
    try {
      const next = await listAnalyticsTables();
      if (id === catalogId.current) setTables(next);
    } catch (error) {
      if (id === catalogId.current) setCatalogError(error instanceof Error ? error.message : t('analytics.queryFailed'));
    } finally {
      if (id === catalogId.current) setCatalogLoading(false);
    }
  }, [t]);

  const loadSaved = useCallback(async () => {
    setSavedError('');
    try {
      const [legacy, workbench] = await Promise.all([
        listSavedQueries({ kind: 'sql', pageSize: 200 }),
        listSavedQueries({ kind: 'analytics', pageSize: 200 }),
      ]);
      setSaved([...legacy.data, ...workbench.data].sort((a, b) => b.id - a.id));
    } catch (error) {
      setSavedError(error instanceof Error ? error.message : t('analytics.queryFailed'));
    }
  }, [t]);

  useEffect(() => {
    void loadCatalog();
    void loadSaved();
    return () => { requestId.current++; catalogId.current++; };
  }, [loadCatalog, loadSaved]);

  const schema = useMemo(() => Object.fromEntries([
    ...SOURCES.map((name) => [name, SEARCH_FIELDS[name].map((column) => column.field)]),
    ...tables.map((table) => [table.schema === 'main' ? table.name : table.schema + '.' + table.name, table.columns.map((column) => column.name)]),
  ]), [tables]);
  const table = tables.find((item) => item.schema + '.' + item.name === selected);
  const draft = JSON.stringify({ mode, source, sql, lucene, parameters, rowLimit, range });
  const generated = useMemo(() => {
    try { return { ...luceneQuery(source, lucene), error: '' }; }
    catch (error) { return { sql: '', parameters: {}, error: error instanceof Error ? error.message : '' }; }
  }, [source, lucene]);

  const run = async () => {
    const id = ++requestId.current;
    const snapshot = draft;
    setRunning(true);
    setQueryError('');
    setResult(null);
    try {
      if (!Number.isInteger(rowLimit) || rowLimit < 1 || rowLimit > 10000) throw new Error('analytics.invalidLimit');
      if (mode === 'lucene' && generated.error) throw new Error(generated.error);
      const response = await searchSql({
        sql: mode === 'sql' ? sql : generated.sql,
        parameters: mode === 'sql' ? parseParameters(parameters) : generated.parameters,
        from: range.from, to: range.to, rowLimit,
      });
      if (id === requestId.current) { setResult(response); setExecuted(snapshot); }
    } catch (error) {
      if (id === requestId.current) setQueryError(message(error));
    } finally {
      if (id === requestId.current) setRunning(false);
    }
  };

  const insert = (text: string) => {
    if (mode !== 'sql') return;
    const view = viewRef.current;
    if (view) {
      const { from, to } = view.state.selection.main;
      view.dispatch({ changes: { from, to, insert: text }, selection: { anchor: from + text.length } });
      view.focus();
    } else setSql((current) => current + text);
  };

  const load = (item: SavedQueryItem) => {
    try {
      if (item.kind === 'sql') { setMode('sql'); setSql(item.sqlText); setParameters('{}'); }
      else {
        const value = parseWorkbench(item.definition);
        setMode(value.mode); setSource(value.source); setSql(value.sql); setLucene(value.lucene);
        setRowLimit(value.rowLimit); setParameters(JSON.stringify(value.parameters, null, 2));
        setRange({ from: new Date(Date.now() - value.rangeMs).toISOString(), to: new Date().toISOString() });
      }
      setSavedOpen(false);
    } catch (error) { toast.error(message(error)); }
  };

  const submitSave = handleSubmit(async (values) => {
    try {
      const definition = { version: 1, mode, source, sql, lucene, rowLimit,
        rangeMs: new Date(range.to).getTime() - new Date(range.from).getTime(), parameters: parseParameters(parameters) };
      parseWorkbench(JSON.stringify(definition));
      await saveQuery({ name: values.name, description: values.description ?? '', kind: 'analytics', definition: JSON.stringify(definition) });
      setSaveOpen(false); toast.success(t('common.save')); await loadSaved();
    } catch (error) { toast.error(message(error)); }
  });

  return (
    <Page>
      <div className="flex h-full min-h-0 flex-1 flex-col gap-3 lg:flex-row">
        <aside hidden={!catalogOpen} className={cn("max-h-[55vh] min-h-0 flex-col rounded-lg border border-border bg-surface lg:max-h-none lg:w-72 lg:shrink-0", catalogOpen ? "flex" : "hidden")}>
          <div className="flex items-center justify-between border-b border-border p-3">
            <span className="flex items-center gap-2 text-sm font-medium"><Database className="size-4" />{t('analytics.catalog')}</span>
            <Button size="sm" variant="link" icon={<RotateCw className="size-3" />} loading={catalogLoading} onClick={() => void loadCatalog()}>{t('analytics.refresh')}</Button>
          </div>
          <section data-testid="analytics-tables-pane" className="flex min-h-0 flex-1 basis-0 flex-col overflow-hidden">
          <div className="shrink-0 border-b border-border p-2"><Input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder={t('analytics.filterTables')} aria-label={t('analytics.filterTables')} /></div>
          <div className="min-h-0 flex-1 overflow-auto p-2">
            <p className="mb-2 text-xs text-muted">{t('analytics.logical')}</p>
            {SOURCES.filter((name) => name.includes(filter.toLowerCase())).map((name) => (
              <div key={name} className="mb-1 flex items-center gap-1">
                <button className={cn('min-w-0 flex-1 rounded px-2 py-1 text-left font-mono text-xs hover:bg-primary-soft', source === name && 'bg-primary-soft text-primary')} onClick={() => setSource(name)}>{'{{' + name + '}}'}</button>
                <Button size="sm" variant="link" icon={<Plus className="size-3" />} disabled={mode !== 'sql'} onClick={() => insert('{{' + name + '}}')}>{t('analytics.insert')}</Button>
              </div>
            ))}
            <p className="mb-2 mt-4 text-xs text-muted">{t('analytics.physical')} · {tables.length}</p>
            {catalogError && <div role="alert" className="mb-2 text-xs text-error">{catalogError}</div>}
            {!catalogLoading && !tables.length && <p className="text-xs text-subtle">{t('common.empty')}</p>}
            {tables.filter((item) => (item.schema + '.' + item.name).toLowerCase().includes(filter.toLowerCase())).map((item) => {
              const key = item.schema + '.' + item.name;
              return <button key={key} onClick={() => { setSelected(key); setFieldFilter(''); }} className={cn('block w-full truncate rounded px-2 py-1.5 text-left font-mono text-xs hover:bg-primary-soft', selected === key && 'bg-primary-soft text-primary')}>{item.name}<span className="ml-2 text-subtle">{item.columns.length}</span></button>;
            })}
          </div>
          </section>
          {table && <section data-testid="analytics-schema-pane" className="flex min-h-0 flex-1 basis-0 flex-col overflow-hidden border-t border-border">
            <div className="shrink-0 border-b border-border p-2"><Input value={fieldFilter} onChange={(event) => setFieldFilter(event.target.value)} placeholder={t('analytics.filterFields')} aria-label={t('analytics.filterFields')} /></div>
            <div className="min-h-0 flex-1 overflow-auto p-3">
            <p className="break-all font-mono text-xs font-medium">{table.schema}.{table.name}</p>
            <p className="my-2 text-xs text-muted">{table.domain ?? t('analytics.other')}{table.day ? ' · ' + formatDateTime(table.day) : ''}</p>
            <div className="mb-2 flex flex-wrap gap-1">
              <Button size="sm" variant="link" icon={<Plus className="size-3" />} disabled={mode !== 'sql'} onClick={() => insert(quoteIdentifier(table.schema) + '.' + quoteIdentifier(table.name))}>{t('analytics.insertTable')}</Button>
              <Button size="sm" variant="link" icon={<Plus className="size-3" />} onClick={() => {
                const target = quoteIdentifier(table.schema) + '.' + quoteIdentifier(table.name);
                setExample('SELECT * FROM ' + target + (table.columns.some((column) => column.name === 'ts') ? '\nWHERE ts >= $start AND ts < $end\nORDER BY ts DESC' : ''));
              }}>{t('analytics.example')}</Button>
            </div>
            {table.columns.filter((column) => column.name.toLowerCase().includes(fieldFilter.trim().toLowerCase())).map((column) => <div key={column.name} className="flex items-start justify-between gap-2 border-t border-border/60 py-1.5 text-xs">
              <Button size="sm" variant="link" icon={<Plus className="size-3" />} disabled={mode !== 'sql'} onClick={() => insert(quoteIdentifier(column.name))}>{column.name}</Button>
              <span className="pt-1 text-right font-mono text-subtle">{column.type}<br />{column.nullable ? 'NULL' : 'NOT NULL'}</span>
            </div>)}
            {!table.columns.some((column) => column.name.toLowerCase().includes(fieldFilter.trim().toLowerCase())) && <p className="text-xs text-subtle">{t('common.empty')}</p>}
            </div>
          </section>}
        </aside>

        <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-3">
          <section className="rounded-lg border border-border bg-surface">
            <div data-testid="analytics-query-bar" className="flex flex-col gap-2 border-b border-border p-2">
              <div className="flex flex-wrap items-center gap-2">
                <Button size="sm" variant="link" icon={<PanelLeft className="size-3" />} aria-expanded={catalogOpen} onClick={() => setCatalogOpen(!catalogOpen)}>{t('analytics.catalog')}</Button>
                {mode === 'lucene' && <Select className="w-32 shrink-0" value={source} onValueChange={(value) => setSource(value as ChartSource)} options={SOURCES.map((name) => ({ value: name, label: name }))} />}
                <div className="min-w-0 flex-1" />
                <Button size="sm" variant="link" icon={<Upload className="size-3" />} onClick={() => setSavedOpen(true)}>{t('analytics.saved')}</Button>
                {canWrite && <Button size="sm" icon={<Save className="size-3" />} onClick={() => { reset({ name: '', description: '' }); setSaveOpen(true); }}>{t('analytics.save')}</Button>}
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <Select className="w-24 shrink-0" aria-label={t('charts.patterns.syntax')} testId="analytics-mode" value={mode} onValueChange={(value) => setMode(value as 'sql' | 'lucene')} options={[{ value: 'sql', label: 'SQL' }, { value: 'lucene', label: 'Lucene' }]} />
                <div className="min-w-0 flex-[1_1_16rem]">
                  {mode === 'lucene'
                    ? <WhereSuggestInput className="w-full" domain={source} syntax="lucene" value={lucene} onChange={setLucene} onRun={() => void run()} from={range.from} to={range.to} denoise={false} testId="analytics-lucene" ariaLabel={t('analytics.luceneQuery')} placeholder={t('analytics.lucenePlaceholder')} />
                    : <span className="text-xs text-subtle">{t('analytics.sql')}</span>}
                </div>
                <div className="ml-auto flex flex-wrap items-center gap-2">
                  <TimeRangePicker value={range} onChange={setRange} allowLive={false} />
                  <label className="flex items-center gap-1.5 whitespace-nowrap text-xs text-muted">{t('analytics.rowLimit')}<InputNumber aria-label={t('analytics.rowLimit')} value={rowLimit} min={1} max={10000} className="w-20" onChange={(event) => setRowLimit(Number(event.target.value))} /></label>
                  <Button size="sm" variant="primary" loading={running} icon={<Play className="size-3" />} onClick={() => void run()}>{t('analytics.run')}</Button>
                </div>
              </div>
            </div>
            <div className="p-3">
              {mode === 'sql' ? <>
                <div data-testid="analytics-sql"><SqlEditor value={sql} onChange={setSql} onRun={() => void run()} schema={schema} parameters={(() => { try { return Object.keys(parseParameters(parameters)); } catch { return []; } })()} logicalTables={SOURCES} viewRef={viewRef} height="190px" /></div>
                <p className="mt-2 text-xs text-subtle">{t('analytics.sqlHint')}</p>
                <details className="mt-2 text-xs text-muted"><summary className="cursor-pointer">{t('designer.parameters')}</summary><Textarea aria-label={t('designer.parameters')} value={parameters} onChange={(event) => setParameters(event.target.value)} rows={3} className="mt-2 font-mono" /></details>
              </> : <>
                <p className="mt-3 text-xs text-subtle">{t('analytics.luceneHint')}</p>
                {generated.error && <p role="alert" className="mt-2 text-xs text-error">{generated.error}</p>}
                <Button size="sm" variant="link" icon={<Database className="size-3" />} onClick={() => setPreview(!preview)}>{t('charts.previewSql')}</Button>
                {preview && <><SqlEditor value={generated.sql} readOnly height="140px" /><pre className="overflow-auto text-xs text-muted">{JSON.stringify(generated.parameters, null, 2)}</pre></>}
              </>}
            </div>
          </section>
          <section className="flex min-h-64 min-w-0 flex-1 flex-col overflow-hidden rounded-lg border border-border bg-surface">
            <header className="flex flex-wrap items-center gap-3 border-b border-border px-3 py-2 text-sm">
              {t('analytics.result')}
              {result && <span className="text-xs text-muted">{t('analytics.rows', { count: result.rows.length })} · {t('analytics.elapsed', { ms: result.elapsedMs })}{result.truncated ? ' · ' + t('analytics.truncated') : ''}</span>}
              {result && executed !== draft && <span className="text-xs text-warning">{t('analytics.stale')}</span>}
            </header>
            <div className="min-h-0 flex-1 overflow-auto p-3">
              {queryError && <p role="alert" className="text-sm text-error">{queryError}</p>}
              {running && <p role="status" className="text-sm text-muted">{t('analytics.running')}</p>}
              {!running && !queryError && (!result || !result.rows.length) && <p className="text-sm text-subtle">{t('common.empty')}</p>}
              {result && <table className="w-full border-collapse text-xs"><thead><tr>{result.columns.map((column, index) => <th key={index} className="sticky top-0 border-b border-border bg-surface-2 px-3 py-2 text-left font-medium">{column}</th>)}</tr></thead><tbody>{result.rows.map((row, index) => <tr key={index}>{row.map((cell, column) => <td key={column} className="max-w-96 break-words border-b border-border/60 px-3 py-2 font-mono">{cell === null || cell === undefined ? 'NULL' : typeof cell === 'object' ? JSON.stringify(cell) : String(cell)}</td>)}</tr>)}</tbody></table>}
            </div>
          </section>
        </div>
      </div>
      <Modal open={Boolean(example)} onOpenChange={(open) => { if (!open) setExample(''); }} title={t('analytics.example')}>
        <SqlEditor value={example} readOnly height="180px" />
      </Modal>
      <Modal open={savedOpen} onOpenChange={setSavedOpen} title={t('analytics.saved')}>
        {savedError && <p role="alert" className="text-error">{savedError}</p>}
        <Button size="sm" variant="link" icon={<RotateCw className="size-3" />} onClick={() => void loadSaved()}>{t('analytics.refresh')}</Button>
        <p className="my-2 text-xs text-subtle">{t('analytics.savedHint')}</p>
        {!saved.length && <p className="text-sm text-muted">{t('common.empty')}</p>}
        {saved.map((item) => <div key={item.id} className="flex items-center justify-between gap-2 border-t border-border py-2">
          <div className="min-w-0"><p className="truncate text-sm">{item.name}</p><p className="text-xs text-subtle">{item.kind}</p></div>
          <div className="text-subtle"><span className="text-xs">{t('common.createTime')}</span><AuditCell time={item.createTime} user={item.createUserId ? String(item.createUserId) : undefined} /></div>
          <div className="text-subtle"><span className="text-xs">{t('common.updateTime')}</span><AuditCell time={item.updateTime} user={item.updateUserId ? String(item.updateUserId) : undefined} /></div>
          <Button size="sm" variant="link" icon={<Upload className="size-3" />} onClick={() => load(item)}>{t('analytics.load')}</Button>
          {canWrite && <Popconfirm title={t('common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={async () => { await deleteSavedQuery(item.id); await loadSaved(); }}><Button size="sm" variant="link" danger icon={<Trash2 className="size-3" />}>{t('common.delete')}</Button></Popconfirm>}
        </div>)}
      </Modal>
      <Modal open={saveOpen} onOpenChange={setSaveOpen} title={t('analytics.save')} footer={<><Button onClick={() => setSaveOpen(false)}>{t('common.cancel')}</Button><Button variant="primary" loading={isSubmitting} onClick={() => void submitSave()}>{t('common.save')}</Button></>}>
        <form onSubmit={(event) => { event.preventDefault(); void submitSave(); }} className="flex flex-col gap-3">
          <Field label={t('common.name')} required error={errors.name?.message} htmlFor="saved-query-name"><Input id="saved-query-name" {...register('name', { required: t('common.name'), maxLength: 64 })} /></Field>
          <Field label={t('common.description')} htmlFor="saved-query-description"><Input id="saved-query-description" {...register('description', { maxLength: 256 })} /></Field>
        </form>
      </Modal>
    </Page>
  );
}
