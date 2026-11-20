import { useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, Eye, Pencil, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { getDashboard, previewPanel, resolveDashboardRange } from '@/api/dashboards';
import type { PanelConfig, PanelData } from '@/api/types';
import { Button, Spin, TimeRangePicker, type TimeRange } from '@/design-system';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { useDashboardSession } from './DashboardSession';
import { normalizeDashboard, appendPanel } from './dashboardLayout';
import { applyGroup, findPanel, leafPanels, replacePanel } from './dashboardGroups';
import { PanelDesignerPage } from './PanelDesignerPage';
import { ChartPanelEditor } from './ChartPanelEditor';
import { GroupEditor } from './GroupEditor';
import { PanelCardBody } from './PanelCard';
import { GroupCard } from './GroupCard';
import { PanelEditGuard } from './PanelEditGuard';

/** URL 决定编辑器类型与只读预览；应用仍只修改父路由内存草稿. */
export function PanelRoutePage({ preview = false }: { preview?: boolean }) {
  const { t } = useTranslation();
  const { id, panelId } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const session = useDashboardSession();
  const canWrite = useAuthStore(state => canWriteRole(state.role));
  const [ready, setReady] = useState(false);
  const [error, setError] = useState('');
  const [range, setRange] = useState<TimeRange>(() => session?.current?.range ?? resolveDashboardRange());
  const [data, setData] = useState<Record<string, PanelData>>({});
  const [loading, setLoading] = useState(false);
  const [run, setRun] = useState(0);
  const kind = params.get('kind') ?? 'custom';
  const back = () => navigate(`/dashboards/${id}?${new URLSearchParams([...params].filter(([key]) => key.startsWith('tab.')).concat([['edit', '1']])).toString()}`);
  useEffect(() => {
    let active = true;
    setReady(false); setError('');
    void (async () => {
      try {
        if (!session) throw new Error(t('designer.invalid'));
        if (session.current?.draft.item.id !== Number(id)) {
          const item = await getDashboard(Number(id));
          if (!active) return;
          const config = normalizeDashboard(item.configJson);
          session.current = { snapshot: { item, config }, draft: { item, config }, range: resolveDashboardRange(config.time), refreshSeconds: config.refreshSeconds, editing: true };
        }
        if (active) { setRange(session.current.range); setReady(true); }
      } catch (reason) { if (active) setError(String(reason)); }
    })();
    return () => { active = false; };
  }, [id, session, t]);
  const panel = ready && panelId && session?.current ? findPanel(session.current.draft.config, panelId) : undefined;
  useEffect(() => {
    if (!preview || !panel) return;
    let active = true;
    setLoading(true); setData({});
    const targets = panel.type === 'group' ? leafPanels(panel.panels ?? []) : [panel];
    void Promise.all(targets.map(async target => {
      try { return await previewPanel(target, range.from, range.to); }
      catch (reason) { return { id: target.id, series: [], columns: [], rows: [], elapsedMs: 0, error: String(reason) } as PanelData; }
    })).then(results => { if (active) setData(Object.fromEntries(results.map(result => [result.id, result]))); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [preview, panel, range.from, range.to, run]);
  if (error || (ready && panelId && !panel) || (!panelId && !['custom', 'explorer', 'group'].includes(kind))) return <div className="space-y-3 p-3"><p role="alert" className="text-error">{error || t('designer.unsupported')}</p><Button onClick={back}>{t('dashboard.back')}</Button></div>;
  if (!ready || !session?.current) return <Spin />;
  const apply = (component: PanelConfig) => {
    const state = session.current!;
    state.draft = { ...state.draft, config: component.type === 'group' ? applyGroup(state.draft.config, component)
      : findPanel(state.draft.config, component.id) ? replacePanel(state.draft.config, component) : appendPanel(state.draft.config, component) };
    state.editing = true;
  };
  if (preview && panel) return <div className="space-y-3 p-3">
    <div className="flex flex-wrap items-center gap-2">
      <Button icon={<ArrowLeft className="size-4" />} onClick={back}>{t('dashboard.back')}</Button>
      <TimeRangePicker allowLive={false} value={range} onChange={setRange} />
      <Button icon={<RefreshCw className="size-4" />} loading={loading} onClick={() => setRun(value => value + 1)}>{t('dashboard.run')}</Button>
      {canWrite && <Button icon={<Pencil className="size-4" />} onClick={() => navigate(`/dashboards/${id}/panels/${encodeURIComponent(panel.id)}/edit?${params}`)}>{t('common.edit')}</Button>}
    </div>
    <div className="h-[560px] min-w-0 rounded border border-border bg-surface p-3">{panel.type === 'group'
      ? <GroupCard config={panel} data={data} loading={loading} />
      : <PanelCardBody config={panel} data={data[panel.id]} loading={loading} />}</div>
  </div>;
  if (panel?.query?.kind === 'custom' || (!panelId && kind === 'custom')) return <PanelDesignerPage />;
  return <PanelEditGuard key={panelId ?? kind}>{allowLeave => <div className="p-3">
    {panel && <Button icon={<Eye className="size-4" />} onClick={() => navigate(`/dashboards/${id}/panels/${encodeURIComponent(panel.id)}/preview?${params}`)}>{t('dashboard.preview')}</Button>}
    {panel?.type === 'group' || (!panelId && kind === 'group')
      ? <GroupEditor config={session.current!.draft.config} initial={panel} onPreview={panel ? () => navigate(`/dashboards/${id}/panels/${encodeURIComponent(panel.id)}/preview?${params}`) : undefined} onClose={back} onApply={component => { allowLeave(); apply(component); back(); }} />
      : <ChartPanelEditor initial={panel} range={range} onClose={back} onPreview={panel ? () => navigate(`/dashboards/${id}/panels/${encodeURIComponent(panel.id)}/preview?${params}`) : undefined} onApply={component => { allowLeave(); apply(component); }} />}
  </div>}</PanelEditGuard>;
}
