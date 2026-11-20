import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import { ArrowLeft, Play, RefreshCw, Monitor, Copy } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { fetchRumProjects, searchRumSessions, fetchSessionEvents, type SessionEvent } from '@/api/rum';
import type { RumSessionSummary, RumProjectItem } from '@/api/types';
import { Button, DataTable, Empty, Input, Page, Select, Tag, Tabs, TabsContent, TabsList, TabsTrigger, TimeRangePicker, toast, type TableColumn, type TimeRange } from '@/design-system';
import { formatDateTime } from '@/utils/format';
import { SessionSetup } from './SessionSetup';
import { PerformanceView, HeatmapView, LiveView, SitemapView, ReplayView } from './RumPage';

function defaultRange(): TimeRange {
  const to = new Date();
  return { from: new Date(to.getTime() - 3600000).toISOString(), to: to.toISOString(), preset: '1h' };
}

/** Client Session：统一查询工具栏、服务端列表与会话详情。 */
export function ClientSessionsPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const [initialRange] = useState(defaultRange);
  const range = { from: params.get('from') || initialRange.from, to: params.get('to') || initialRange.to };
  const [draftRange, setDraftRange] = useState<TimeRange>(range);
  const [search, setSearch] = useState(params.get('search') || '');
  const [projects, setProjects] = useState<RumProjectItem[]>([]);
  const [projectError, setProjectError] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const projectKey = params.get('project') || projects[0]?.projectKey || '';
  const selected = params.get('session') || '';
  const tab = params.get('tab') || 'sessions';
  const page = Math.max(1, Number(params.get('page')) || 1);
  const [result, setResult] = useState<{ count: number; items: RumSessionSummary[] }>({ count: 0, items: [] });
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);

  const update = (patch: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params);
    Object.entries(patch).forEach(([key, value]) => value ? next.set(key, value) : next.delete(key));
    setParams(next);
  };

  useEffect(() => {
    let cancelled = false;
    setProjectError(false);
    void fetchRumProjects().then((data) => { if (!cancelled) setProjects(data.data); })
      .catch(() => { if (!cancelled) setProjectError(true); });
    return () => { cancelled = true; };
  }, [refresh]);

  useEffect(() => {
    setSearch(params.get('search') || '');
    setDraftRange({ from: range.from, to: range.to });
  }, [params, range.from, range.to]);

  const executedSearch = params.get('search') || '';
  const errors = params.get('errors') || '';
  const replay = params.get('replay') || '';
  const sort = params.get('sort') || 'lastSeen';
  useEffect(() => {
    if (!projectKey) return;
    let cancelled = false;
    setLoading(true);
    setFailed(false);
    void searchRumSessions({ projectKey, ...range, search: executedSearch, page, pageSize: 20, sort,
      hasErrors: errors ? errors === 'yes' : undefined, hasReplay: replay ? replay === 'yes' : undefined })
      .then((data) => { if (!cancelled) setResult(data); })
      .catch(() => { if (!cancelled) { setResult({ count: 0, items: [] }); setFailed(true); } })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectKey, range.from, range.to, executedSearch, errors, replay, sort, page, refresh]);

  const run = () => update({ project: projectKey, search, from: draftRange.from, to: draftRange.to, page: undefined, session: undefined });
  const columns: TableColumn<RumSessionSummary>[] = [
    { title: t('rum.sessionId'), dataIndex: 'sessionId', mono: true, render: (value: string) =>
      <Button variant="link" size="sm" icon={<Play className="size-3" />} onClick={() => update({ session: value, project: projectKey, ...range })}>{value}</Button> },
    { title: t('clientSession.activity'), key: 'activity', render: (_, row) => <div className="font-mono text-xs"><div>{formatDateTime(row.lastSeen)}</div><div className="text-subtle">{row.firstSeen ? formatDateTime(row.firstSeen) : '—'}</div></div> },
    { title: t('clientSession.duration'), key: 'duration', mono: true, render: (_, row) => row.firstSeen ? `${Math.max(0, Math.round((Date.parse(row.lastSeen) - Date.parse(row.firstSeen)) / 1000))}s` : '—' },
    { title: t('rum.pagePath'), dataIndex: 'pagePath', mono: true, ellipsis: true },
    { title: t('rum.domain'), dataIndex: 'domain', mono: true },
    { title: t('rum.userAgent'), dataIndex: 'userAgent', ellipsis: true },
    { title: t('clientSession.pageViews'), dataIndex: 'pageViews', mono: true },
    { title: t('rum.errors'), dataIndex: 'errorCount', render: (value: number) => <Tag tone={value ? 'error' : undefined}>{value ?? 0}</Tag> },
    { title: t('rum.replay'), dataIndex: 'hasReplay', render: (value: boolean) => <Tag>{t(value ? 'clientSession.available' : 'clientSession.unavailable')}</Tag> },
  ];

  return <Page extra={<div className="flex w-full flex-wrap items-center justify-start gap-2">
    <Select testId="rum-project" aria-label={t('rum.selectProject')} className="w-52 shrink-0" value={projectKey}
      options={projects.map((project) => ({ value: project.projectKey, label: project.name }))}
      placeholder={t('rum.selectProject')} onValueChange={(value) => update({ project: value, session: undefined, page: undefined })} />
    <Input className="min-w-48 flex-1" aria-label={t('clientSession.search')} placeholder={t('clientSession.search')} value={search} onChange={(event) => setSearch(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter') run(); }} />
    <TimeRangePicker value={draftRange} onChange={setDraftRange} allowLive={false} showAbsoluteRange />
    <Button variant="primary" icon={<Play className="size-3.5" />} onClick={run}>{t('clientSession.run')}</Button>
    <Button icon={<RefreshCw className="size-3.5" />} onClick={() => setRefresh((value) => value + 1)}>{t('common.refresh')}</Button>
    <SessionSetup projectKey={projectKey} project={projects.find((project) => project.projectKey === projectKey)} onCreated={(key) => { update({ project: key, session: undefined, page: undefined }); setRefresh((value) => value + 1); }} />
  </div>}>
    {projectError ? <ErrorState retry={() => setRefresh((value) => value + 1)} /> : !projectKey ? <SetupGuide /> :
      selected ? <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-auto">
        <div className="flex flex-wrap items-center gap-3">
          <Button icon={<ArrowLeft className="size-3.5" />} onClick={() => update({ session: undefined })}>{t('clientSession.back')}</Button>
          <span className="font-mono text-sm">{selected}</span>
          <Button icon={<Copy className="size-3.5" />} onClick={() => void navigator.clipboard.writeText(window.location.href).then(() => toast.success(t('clientSession.copied'))).catch(() => toast.error(t('clientSession.copyFailed')))}>{t('clientSession.share')}</Button>
        </div>
        <SessionDetails key={`${projectKey}:${selected}:${range.from}:${range.to}:${refresh}`} projectKey={projectKey} sessionId={selected} range={range} />
      </div> : <Tabs value={tab} onValueChange={(value) => update({ tab: value })} className="flex min-h-0 flex-1 flex-col gap-3">
        <TabsList>{['sessions', 'performance', 'heatmap', 'live', 'sitemap'].map((value) =>
          <TabsTrigger key={value} value={value}>{t(`rum.tab${value[0].toUpperCase()}${value.slice(1)}`)}</TabsTrigger>)}</TabsList>
        <TabsContent value="sessions" className="flex min-h-0 flex-1 flex-col gap-2">
          <div className="flex flex-wrap items-center gap-2">
            <Select className="w-44 shrink-0" aria-label={t('rum.errors')} value={errors || 'all'} onValueChange={(value) => update({ errors: value === 'all' ? undefined : value, page: undefined })}
              options={[{ value: 'all', label: t('clientSession.allErrors') }, { value: 'yes', label: t('clientSession.withErrors') }, { value: 'no', label: t('clientSession.withoutErrors') }]} />
            <Select className="w-44 shrink-0" aria-label={t('rum.replay')} value={replay || 'all'} onValueChange={(value) => update({ replay: value === 'all' ? undefined : value, page: undefined })}
              options={[{ value: 'all', label: t('clientSession.allReplay') }, { value: 'yes', label: t('clientSession.available') }, { value: 'no', label: t('clientSession.unavailable') }]} />
            <Select className="w-44 shrink-0" aria-label={t('clientSession.sort')} value={sort} onValueChange={(value) => update({ sort: value, page: undefined })}
              options={['lastSeen', 'firstSeen', 'duration', 'errors'].map((value) => ({ value, label: t(`clientSession.sort_${value}`) }))} />
            <span className="font-mono text-xs text-subtle">{result.count} {t('clientSession.results')}</span>
          </div>
          <p className="text-xs text-subtle">{t('clientSession.windowHint')}</p>
          {failed ? <ErrorState retry={() => setRefresh((value) => value + 1)} /> :
            <DataTable rowKey="sessionId" columns={columns} dataSource={result.items} loading={loading} scrollX={1000}
              emptyText={<SetupGuide compact />} pagination={{ current: page, pageSize: 20, total: result.count, onChange: (value) => update({ page: String(value) }) }} />}
        </TabsContent>
        <TabsContent value="performance" className="overflow-auto"><PerformanceView key={`${projectKey}:${range.from}:${range.to}:${refresh}`} projectKey={projectKey} range={range} /></TabsContent>
        <TabsContent value="heatmap" className="overflow-auto"><HeatmapView key={`${projectKey}:${range.from}:${range.to}:${refresh}`} projectKey={projectKey} range={range} /></TabsContent>
        <TabsContent value="live" className="overflow-auto"><LiveView key={projectKey} projectKey={projectKey} /></TabsContent>
        <TabsContent value="sitemap" className="overflow-auto"><SitemapView key={`${projectKey}:${range.from}:${range.to}:${refresh}`} projectKey={projectKey} range={range} /></TabsContent>
      </Tabs>}
  </Page>;
}

function ErrorState({ retry }: { retry: () => void }) {
  const { t } = useTranslation();
  return <div role="alert" className="rounded border border-error/40 bg-error-soft p-4 text-sm">
    <p>{t('clientSession.loadFailed')}</p><Button onClick={retry} icon={<RefreshCw className="size-3" />}>{t('common.refresh')}</Button>
  </div>;
}

function SetupGuide({ compact = false }: { compact?: boolean }) {
  const { t } = useTranslation();
  return <section className="flex flex-col items-center gap-4 px-4 py-10 text-center">
    <Monitor className="size-8 text-subtle" /><h2 className="text-base font-semibold">{t(compact ? 'clientSession.noResults' : 'clientSession.setup')}</h2>
    <p className="text-sm text-subtle">{t('clientSession.setupHint')}</p>
    {!compact && <ol className="grid gap-4 rounded-lg border border-border bg-surface p-6 text-left">
      {['project', 'probe', 'view'].map((step, index) => <li key={step} className="flex gap-3"><span className="font-mono text-primary">{index + 1}</span><div><p className="text-sm font-medium">{t(`clientSession.step_${step}`)}</p><p className="text-xs text-subtle">{t(`clientSession.step_${step}Hint`)}</p></div></li>)}
    </ol>}
  </section>;
}

function SessionDetails({ projectKey, sessionId, range }: { projectKey: string; sessionId: string; range: { from: string; to: string } }) {
  const { t } = useTranslation();
  const [events, setEvents] = useState<SessionEvent[]>([]);
  const [failed, setFailed] = useState(false);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    void fetchSessionEvents({ projectKey, sessionId, ...range }).then((data) => { if (!cancelled) setEvents(data); }).catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, [projectKey, sessionId, range.from, range.to, refresh]);
  return <div className="grid min-h-0 gap-3 xl:grid-cols-[minmax(0,1fr)_320px]">
    <section className="min-w-0 rounded border border-border bg-surface p-3"><ReplayView projectKey={projectKey} sessionId={sessionId} range={range} /></section>
    <section className="min-w-0 rounded border border-border bg-surface p-3">
      <h3 className="mb-3 text-sm font-medium">{t('clientSession.timeline')}</h3>
      <p className="mb-3 text-xs text-subtle">{t('clientSession.timelineLimit')}</p>
      {failed ? <ErrorState retry={() => setRefresh((value) => value + 1)} /> : events.length === 0 ? <Empty description={t('common.empty')} /> :
        <div className="max-h-[600px] space-y-2 overflow-auto">{events.map((event, index) => <details key={index} className="rounded border border-border p-2 text-xs">
          <summary className="cursor-pointer"><span className="font-mono text-subtle">{formatDateTime(event.timestamp)}</span><div className={event.type === 'error' ? 'text-error' : 'text-fg'}>{event.type} · {event.name || event.pagePath || '—'}</div></summary>
          <p className="mt-2 break-all font-mono">{event.pagePath}</p><pre className="overflow-auto whitespace-pre-wrap break-all">{event.payload}</pre>
        </details>)}</div>}
    </section>
  </div>;
}
