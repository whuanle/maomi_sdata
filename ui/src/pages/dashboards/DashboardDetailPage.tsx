import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import { useBlocker, useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, Download, FileUp, Maximize2, Pencil, Plus, RefreshCw, Save, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { getDashboard, getDashboardData, previewPanel, resolveDashboardRange, saveDashboard } from '@/api/dashboards';
import type { DashboardConfig, DashboardItem, PanelData, PanelConfig } from '@/api/types';
import { Button, ConfirmModal, Empty, Field, Input, Modal, QueryBar, Select, Spin, Textarea, TimeRangePicker, toast, type TimeRange } from '@/design-system';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { cn } from '@/utils/cn';
import { panelQueryIdentity } from '@/pages/charts/panelDefinition';
import { DashboardCanvas } from './DashboardCanvas';
import { appendPanel, normalizeDashboard } from './dashboardLayout';
import { clonePanel, dissolveGroup, leafPanels, movePanelIntoGroup, removePanel } from './dashboardGroups';
import { insertGridItem } from '@/design-system/dashboardGridLayout';
import { buildDashboardExport, buildPanelExport, downloadJson, parsePanelImport, safeFileName } from './transfer';
import { useDashboardSession } from './DashboardSession';

interface Snapshot { item: DashboardItem; config: DashboardConfig }

/** 独立画布与只读展示共用状态；编辑仅更新草稿，整盘保存成功后更新基线. */
export function DashboardDetailPage({ present = false }: { present?: boolean }) {
  const { t } = useTranslation();
  const { id } = useParams();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const canWrite = useAuthStore((state) => canWriteRole(state.role));
  const session = useDashboardSession();
  const restored = session?.current?.draft.item.id === Number(id) ? session.current : null;
  const [snapshot, setSnapshot] = useState<Snapshot | null>(restored?.snapshot ?? null);
  const [draft, setDraft] = useState<Snapshot | null>(restored?.draft ?? null);
  const editing = !present && canWrite && params.get('edit') === '1';
  const setEditing = (value: boolean) => setParams(previous => {
    const next = new URLSearchParams(previous);
    if (value) next.set('edit', '1'); else next.delete('edit');
    return next;
  });
  const openPanel = (suffix: string, kind?: string) => {
    const next = new URLSearchParams(params);
    if (kind) next.set('kind', kind); else next.delete('kind');
    navigate(`/dashboards/${id}/panels/${suffix}?${next}`);
  };
  const [data, setData] = useState<Record<string, PanelData>>({});
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [discard, setDiscard] = useState(false);
  const [range, setRange] = useState<TimeRange>(() => restored?.range ?? ({ ...resolveDashboardRange(), preset: '1h' }));
  const [refreshSeconds, setRefreshSeconds] = useState(restored?.refreshSeconds ?? 0);
  const request = useRef(0);
  const loadGeneration = useRef(0);
  const savingRef = useRef(false);
  const current = useRef({ draft, snapshot, range });
  current.current = { draft, snapshot, range };
  const dirty = Boolean(snapshot && draft && JSON.stringify(snapshot) !== JSON.stringify(draft));
  const blocker = useBlocker(({ currentLocation, nextLocation }) =>
    dirty && !savingRef.current && currentLocation.pathname !== nextLocation.pathname
      && !nextLocation.pathname.startsWith(`/dashboards/${id}/panels/`));
  useEffect(() => {
    if (session && snapshot && draft && draft.item.id === Number(id)) session.current = { snapshot, draft, editing, range, refreshSeconds };
  }, [session, snapshot, draft, editing, range, refreshSeconds]);

  useEffect(() => {
    const generation = ++loadGeneration.current;
    if (session?.current?.draft.item.id === Number(id)) {
      const state = session.current;
      setSnapshot(state.snapshot); setDraft(state.draft);
      setRange(state.range); setRefreshSeconds(state.refreshSeconds);
      return () => { loadGeneration.current++; request.current++; };
    }
    setSnapshot(null); setDraft(null); setError(''); setData({});
    void getDashboard(Number(id)).then((item) => {
      if (loadGeneration.current !== generation) return;
      const config = normalizeDashboard(item.configJson);
      const initial = { item, config };
      setSnapshot(initial); setDraft(initial);
      setRange({ ...resolveDashboardRange(config.time), preset: config.time?.preset === 'custom' ? undefined : config.time?.preset ?? '1h' });
      setRefreshSeconds(config.refreshSeconds || 0);
    }).catch((reason) => { if (loadGeneration.current === generation) setError(reason instanceof Error ? reason.message : String(reason)); });
    return () => { loadGeneration.current++; request.current++; };
    // 路由 id 变化才重新加载，草稿不被查询参数变化重置
  }, [id, present]);

  useEffect(() => {
    if (!dirty) return undefined;
    const leave = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', leave);
    return () => window.removeEventListener('beforeunload', leave);
  }, [dirty]);

  const refresh = useCallback(async (activeRange?: TimeRange) => {
    const state = current.current;
    if (!state.draft || !state.snapshot) return;
    const version = ++request.current;
    const selected = activeRange ?? state.range;
    const nextRange = selected.preset
      ? { ...resolveDashboardRange({ preset: selected.preset }), preset: selected.preset }
      : selected;
    setRange(nextRange); setLoading(true);
    const panels = leafPanels(state.draft.config.panels);
    const base = leafPanels(state.snapshot.config.panels);
    const changed = panels.filter((p) => p.type !== 'markdown').filter((p) => {
      const saved = base.find((item) => item.id === p.id);
      return !saved || panelQueryIdentity(saved) !== panelQueryIdentity(p);
    });
    try {
      const savedResult = await getDashboardData(state.draft.item.id, { from: nextRange.from, to: nextRange.to });
      const result = Object.fromEntries(savedResult.panels.map((item) => [item.id, item]));
      // 草稿查询最多三个并发，单组件失败不会中断其它组件
      let index = 0;
      await Promise.all(Array.from({ length: Math.min(3, changed.length) }, async () => {
        while (index < changed.length && version === request.current) {
          const component = changed[index++];
          try {
            result[component.id] = await previewPanel(component, nextRange.from, nextRange.to);
          } catch (reason) {
            result[component.id] = { id: component.id, series: [], columns: [], rows: [], elapsedMs: 0, error: String(reason) };
          }
        }
      }));
      if (version === request.current) setData(result);
    } catch (reason) {
      if (version === request.current) toast.error(String(reason));
    } finally {
      if (version === request.current) setLoading(false);
    }
  }, []);

  // 仅查询发生变化或基线加载时取数；拖动、resize 不重跑查询
  const queryKey = draft ? leafPanels(draft.config.panels).filter((p) => p.type !== 'markdown').map(panelQueryIdentity).sort().join('|') : undefined;
  useEffect(() => { if (snapshot) void refresh(); }, [snapshot, queryKey, refresh]);
  useEffect(() => {
    if (!snapshot || refreshSeconds === 0) return undefined;
    const timer = window.setInterval(() => { if (!loading) void refresh(); }, refreshSeconds * 1000);
    return () => window.clearInterval(timer);
  }, [snapshot, refreshSeconds, loading, refresh]);

  const updateConfig = (config: DashboardConfig) => setDraft((prev) => prev ? { ...prev, config } : prev);
  const applyLayout = (items: import('@/design-system').DashboardGridItem[]) => updateConfig({ ...draft!.config, panels: items.map(item => {
    const panel = draft!.config.panels.find(panel => panel.id === item.i)!;
    return { ...panel, layout: { x: item.x, y: item.y, w: item.w, h: item.h } };
  }) });
  const save = async () => {
    if (!draft || savingRef.current) return false;
    if (!draft.item.name.trim()) { toast.error(t('dashboard.panelTitleRequired')); return false; }
    savingRef.current = true; setSaving(true);
    const nextConfig = {
      ...draft.config, version: draft.config.version, refreshSeconds,
      time: range.preset ? { preset: range.preset } : { preset: 'custom', from: range.from, to: range.to },
    };
    try {
      await saveDashboard({ ...draft.item, configJson: JSON.stringify(nextConfig), expectedConfigJson: snapshot?.item.configJson });
      const next = { item: { ...draft.item, configJson: JSON.stringify(nextConfig) }, config: nextConfig };
      setSnapshot(next); setDraft(next); setEditing(false);
      toast.success(t('common.save'));
      return true;
    } catch (reason) { toast.error(String(reason)); return false; }
    finally { savingRef.current = false; setSaving(false); }
  };
  const cancel = () => {
    if (snapshot) {
      setDraft(snapshot); setEditing(false);
      setRange({ ...resolveDashboardRange(snapshot.config.time), preset: snapshot.config.time?.preset === 'custom' ? undefined : snapshot.config.time?.preset ?? '1h' });
      setRefreshSeconds(snapshot.config.refreshSeconds);
    }
  };
  const presentDashboard = async () => {
    if (dirty) { toast.info(t('dashboard.saveBeforePresent')); return; }
    navigate(`/dashboards/${id}/present`);
  };
  // 整盘导出所见即所得：导出当前草稿（含未保存修改）
  const exportDashboard = () => {
    if (!draft) return;
    downloadJson(safeFileName(draft.item.name, 'dashboard'), buildDashboardExport(draft.item, draft.config));
  };
  const panelFileRef = useRef<HTMLInputElement>(null);
  const [panelImportOpen, setPanelImportOpen] = useState(false);
  const [panelSource, setPanelSource] = useState('');
  const [panelParsed, setPanelParsed] = useState<PanelConfig | null>(null);
  const applyPanelSource = (text: string) => {
    setPanelSource(text);
    setPanelParsed(null);
    if (!text.trim()) return;
    try { setPanelParsed(parsePanelImport(text)); } catch { /* 提交时提示 dashboard.importInvalid */ }
  };
  const onPanelFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (file) await applyPanelSource(await file.text());
    event.target.value = '';
  };
  const openPanelImport = () => { setPanelSource(''); setPanelParsed(null); setPanelImportOpen(true); };
  const confirmPanelImport = () => {
    let panel = panelParsed;
    if (!panel) {
      try { panel = parsePanelImport(panelSource); } catch { toast.error(t('dashboard.importInvalid')); return; }
    }
    updateConfig(appendPanel(draft!.config, panel));
    setPanelImportOpen(false);
    toast.success(t('dashboard.importPanelSuccess'));
  };
  const exportPanel = (panel: PanelConfig) => {
    downloadJson(safeFileName(panel.title || 'panel', 'panel'), buildPanelExport(panel));
  };

  if (error) return <div className="p-6 text-error" role="alert">{t(error)} <Button onClick={() => navigate('/dashboards')}>{t('dashboard.back')}</Button></div>;
  if (!draft) return <div className="flex h-full items-center justify-center"><Spin /></div>;
  return <div className={cn('flex min-h-0 min-w-0 flex-col gap-2 bg-bg px-3 pt-2 pb-3', present && 'min-h-screen')}>
    <QueryBar className="min-w-0 border-b border-border pb-2">
      <Button variant="link" size="sm" className="shrink-0" icon={<ArrowLeft className="size-4" />} onClick={() => navigate(present ? `/dashboards/${id}` : '/dashboards')}>{t('dashboard.back')}</Button>
      {editing ? <Input aria-label={t('dashboard.name')} className="w-80 min-w-0 max-w-full" value={draft.item.name} maxLength={64} onChange={(event) => setDraft({ ...draft, item: { ...draft.item, name: event.target.value } })} />
        : <span className="max-w-sm truncate text-base font-semibold text-fg" title={draft.item.name}>{draft.item.name}</span>}
      <div className="flex flex-wrap items-center gap-2">
        {editing && <><Button icon={<X className="size-4" />} onClick={() => dirty ? setDiscard(true) : cancel()} disabled={saving}>{t('common.cancel')}</Button>
          <Button variant="primary" loading={saving} icon={<Save className="size-4" />} onClick={() => void save()}>{t('common.save')}</Button></>}
        {!present && <Button icon={<Maximize2 className="size-4" />} onClick={() => void presentDashboard()}>{t('dashboard.present')}</Button>}
        {!present && draft && <Button icon={<Download className="size-4" />} onClick={exportDashboard}>{t('dashboard.export')}</Button>}
      </div>
      {dirty && <span className="text-xs text-warning">{t('dashboard.unsaved')}</span>}
      <TimeRangePicker allowLive={false} value={range} onChange={(next) => {
        setRange(next);
        if (editing) updateConfig({ ...draft.config, time: next.preset ? { preset: next.preset } : { preset: 'custom', from: next.from, to: next.to } });
        void refresh(next);
      }} />
      <Select className="w-32" aria-label={t('dashboard.refresh')} value={String(refreshSeconds)} onValueChange={(value) => {
        setRefreshSeconds(Number(value));
        if (editing) updateConfig({ ...draft.config, refreshSeconds: Number(value) });
      }} options={[0, 10, 30, 60].map((value) => ({ value: String(value), label: value === 0 ? t('dashboard.refreshOff') : `${value}s` }))} />
      <Button loading={loading} icon={<RefreshCw className="size-4" />} onClick={() => void refresh()}>{t('dashboard.run')}</Button>
      {!present && !editing && canWrite && <Button icon={<Pencil className="size-4" />} onClick={() => setEditing(true)}>{t('dashboard.editMode')}</Button>}
      {editing && <Button icon={<Plus className="size-4" />} onClick={() => openPanel('new', 'group')}>{t('dashboard.addGroup')}</Button>}
      {editing && <Button icon={<FileUp className="size-4" />} onClick={openPanelImport}>{t('dashboard.importPanel')}</Button>}
      {editing && <Button icon={<Plus className="size-4" />} onClick={() => navigate(`/dashboards/${id}/panels/new`)}>{t('designer.create')}</Button>}
      {editing && <Button data-testid="add-component" variant="primary" icon={<Plus className="size-4" />} onClick={() => openPanel('new', 'explorer')}>{t('dashboard.addComponent')}</Button>}
    </QueryBar>
    <DashboardCanvas config={draft.config} data={data} editable={editing && !saving} loading={loading}
      onLayout={applyLayout}
      onExport={exportPanel}
      onGroupDrop={(panelId, groupId) => updateConfig(movePanelIntoGroup(draft.config, panelId, groupId))}
      onPreview={!present ? component => openPanel(encodeURIComponent(component.id) + '/preview') : undefined}
      onGroup={component => openPanel(encodeURIComponent(component.id) + '/edit')}
      onDissolve={(component) => updateConfig(dissolveGroup(draft.config, component))}
      onEdit={component => openPanel(encodeURIComponent(component.id) + '/edit')}
      onCopy={(component) => updateConfig(appendPanel(draft.config, clonePanel(component, t('dashboard.copy'))))}
      onDelete={(componentId) => updateConfig(removePanel(draft.config, componentId))}
      onMove={(component, direction) => {
        const items = draft.config.panels.map(p => ({ i: p.id, x: p.layout?.x ?? 0, y: p.layout?.y ?? 0, w: p.layout?.w ?? 6, h: p.layout?.h ?? 4 })).sort((a, b) => a.y - b.y || a.x - b.x);
        const index = items.findIndex(p => p.i === component.id);
        const other = items[index + direction];
        if (other) applyLayout(insertGridItem(items, component.id, other.i, direction > 0));
      }} />
    {draft.config.panels.length === 0 && <div className="flex min-h-64 flex-col items-center justify-center gap-4 rounded-lg border border-dashed border-border bg-surface">
      <Empty description={t('dashboard.emptyCanvas')} />
      {!present && canWrite && <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => openPanel('new', 'explorer')}>{t('dashboard.addComponent')}</Button>}
    </div>}
    <ConfirmModal open={discard} onOpenChange={setDiscard} title={t('dashboard.discard')} content={t('dashboard.discardHint')} okText={t('dashboard.discard')} cancelText={t('common.cancel')} onOk={cancel} />
    <Modal open={panelImportOpen} onOpenChange={setPanelImportOpen} title={t('dashboard.importPanel')} footer={<><Button onClick={() => setPanelImportOpen(false)}>{t('common.cancel')}</Button><Button variant="primary" onClick={confirmPanelImport}>{t('dashboard.import')}</Button></>}>
      <Field label={t('dashboard.importSource')} error={panelSource.trim() && !panelParsed ? t('dashboard.importInvalid') : undefined}>
        <input ref={panelFileRef} type="file" accept=".json,application/json" className="hidden" onChange={(event) => void onPanelFile(event)} />
        <div className="flex items-center gap-2">
          <Button icon={<FileUp className="size-4" />} onClick={() => panelFileRef.current?.click()}>{t('dashboard.importFile')}</Button>
          {panelParsed && <span className="text-xs text-muted">{panelParsed.title}</span>}
        </div>
        <Textarea aria-label={t('dashboard.importSource')} className="mt-2 min-h-32" placeholder={t('dashboard.importPanelPlaceholder')} value={panelSource} onChange={(event) => applyPanelSource(event.target.value)} />
      </Field>
    </Modal>
    <ConfirmModal open={blocker.state === 'blocked'} onOpenChange={(open) => { if (!open && blocker.state === 'blocked') blocker.reset(); }} title={t('dashboard.unsaved')} content={t('dashboard.discardHint')} okText={t('dashboard.discard')} cancelText={t('common.cancel')} onOk={() => { if (blocker.state === 'blocked') blocker.proceed(); }} />
  </div>;
}
