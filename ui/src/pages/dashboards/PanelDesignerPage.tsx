import { useEffect, useRef, useState } from 'react';
import { useBlocker, useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, Eye, Play, Plus, Save, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { getDashboard, previewPanel, resolveDashboardRange } from '@/api/dashboards';
import type { CustomPanelNode, PanelConfig, PanelData, PanelType } from '@/api/types';
import { compileCustomPanel } from './customNodeBuilder';
import { CustomNodeEditor } from './CustomNodeEditor';
import { Button, Checkbox, ConfirmModal, Field, Input, Popconfirm, Select, Spin, TimeRangePicker, type TimeRange } from '@/design-system';
import { useDashboardSession } from './DashboardSession';
import { appendPanel, normalizeDashboard } from './dashboardLayout';
import { findPanel, replacePanel } from './dashboardGroups';
import { PanelCardBody } from './PanelCard';
import { newCustomNode, newCustomPanel, validateCustomPanel } from './customPanel';
import { NumberSettingsDrawer } from '@/pages/charts/NumberSettingsDrawer';
import { DEFAULT_NUMBER_SETTINGS } from '@/pages/charts/numberTypes';
import { TableSettings } from '@/pages/charts/TableSettings';
import { BarDisplayModal } from '@/pages/charts/BarDisplayModal';
import { DEFAULT_VALUE_FORMAT } from '@/pages/charts/barFormat';

export function PanelDesignerPage() {
  const { t } = useTranslation();
  const { id, panelId } = useParams();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const setPosition = (name: string, value?: string) => setParams(previous => {
    const next = new URLSearchParams(previous);
    if (value) next.set(name, value); else next.delete(name);
    return next;
  }, { replace: true });
  const session = useDashboardSession();
  const [panel, setPanel] = useState<PanelConfig | null>(null);
  const [range, setRange] = useState<TimeRange>(() => session?.current?.range ?? resolveDashboardRange());
  const [data, setData] = useState<PanelData>();
  const [error, setError] = useState('');
  const [running, setRunning] = useState(false);
  const displayOpen = params.get('settings') === 'display';
  const setDisplayOpen = (open: boolean) => setPosition('settings', open ? 'display' : undefined);
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const nodeElements = useRef<Record<string, HTMLElement | null>>({});
  const focusedNode = params.get('node');
  useEffect(() => {
    if (focusedNode && panel?.query?.custom?.nodes.some(node => node.ref === focusedNode)) {
      nodeElements.current[focusedNode]?.scrollIntoView?.({ block: 'nearest' });
    }
  }, [focusedNode, panel?.id]);
  const [executed, setExecuted] = useState('');
  const baseline = useRef('');
  const request = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const [parameterErrors, setParameterErrors] = useState<Record<string, boolean>>({});
  const applied = useRef(false);
  const translate = useRef(t);
  translate.current = t;
  let compiledPanel = panel;
  let compilationError = '';
  if (panel) {
    try { compiledPanel = compileCustomPanel(panel, range); }
    catch (reason) { compilationError = reason instanceof Error ? reason.message : String(reason); }
  }
  const key = JSON.stringify([compiledPanel?.query?.custom?.nodes.map(node => ({ ...node, editor: undefined, alias: undefined })), compiledPanel?.query?.custom?.outputs, range, compilationError]);
  const activeKey = useRef(key);
  activeKey.current = key;
  const dirty = Boolean(panel && JSON.stringify(panel) !== baseline.current) || Object.values(parameterErrors).some(Boolean);
  const blocker = useBlocker(({ currentLocation, nextLocation }) => !applied.current
    && (dirty || (nextLocation.pathname !== `/dashboards/${id}` && Boolean(session?.current && JSON.stringify(session.current.snapshot) !== JSON.stringify(session.current.draft))))
    && currentLocation.pathname !== nextLocation.pathname);
  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        if (!session) throw new Error(translate.current('designer.invalid'));
        if (session.current?.draft.item.id !== Number(id)) {
          const item = await getDashboard(Number(id));
          if (!active) return;
          const config = normalizeDashboard(item.configJson);
          session.current = { snapshot: { item, config }, draft: { item, config }, range: resolveDashboardRange(config.time), refreshSeconds: config.refreshSeconds, editing: true };
        }
        const initial = panelId ? findPanel(session.current.draft.config, panelId) : newCustomPanel();
        if (!initial || initial.query?.kind !== 'custom' || initial.query.custom?.version !== 1) throw new Error(translate.current('designer.unsupported'));
        const copy = structuredClone(initial);
        baseline.current = JSON.stringify(copy);
        setPanel(copy); setRange(session.current.range);
      } catch (reason) { if (active) setError(String(reason)); }
    })();
    return () => { active = false; request.current++; controller.current?.abort(); };
  }, [id, panelId, session]);
  useEffect(() => {
    const leave = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    if (dirty || (session?.current && JSON.stringify(session.current.snapshot) !== JSON.stringify(session.current.draft))) window.addEventListener('beforeunload', leave);
    return () => window.removeEventListener('beforeunload', leave);
  }, [dirty, session]);
  const update = (patch: Partial<PanelConfig>) => setPanel(previous => previous && ({ ...previous, ...patch }));
  const updateNode = (ref: string, patch: Partial<CustomPanelNode>) => {
    setPanel(previous => previous?.query?.custom ? { ...previous, query: { ...previous.query, custom: { ...previous.query.custom,
      nodes: previous.query.custom.nodes.map(node => node.ref === ref ? { ...node, ...patch } : node) } } } : previous);
  };
  const run = async () => {
    if (!panel) return;
    if (Object.values(parameterErrors).some(Boolean)) { setError(t('designer.invalidParameters')); return; }
    if (compilationError) { setError(t(compilationError)); return; }
    const invalid = validateCustomPanel(compiledPanel!);
    if (invalid) { setError(t(invalid)); return; }
    const generation = ++request.current;
    controller.current?.abort();
    controller.current = new AbortController();
    setRunning(true); setError('');
    try {
      const result = await previewPanel(compiledPanel!, range.from, range.to, 500, controller.current.signal);
      if (generation !== request.current || activeKey.current !== key) return;
      setData(result); setExecuted(key);
      if (result.error) setError(result.error);
    } catch (reason) { if (generation === request.current && activeKey.current === key) setError(String(reason)); }
    finally { if (generation === request.current) setRunning(false); }
  };
  const back = () => navigate(`/dashboards/${id}?${new URLSearchParams([...params].filter(([key]) => key.startsWith('tab.')).concat([['edit', '1']]))}`);
  const apply = () => {
    if (!panel || !session?.current || executed !== key || data?.error || error || compilationError || validateCustomPanel(compiledPanel!) || Object.values(parameterErrors).some(Boolean)) return;
    const state = session.current;
    const config = state.draft.config;
    state.draft = { ...state.draft, config: panelId
      ? replacePanel(config, compiledPanel!)
      : appendPanel(config, compiledPanel!) };
    state.editing = true;
    applied.current = true; back();
  };
  if (!panel) return error ? <p role="alert" className="p-4 text-error">{error}</p> : <Spin />;
  const definition = panel.query!.custom!;
  const add = (kind: CustomPanelNode['kind']) => {
    let index = 0;
    while (definition.nodes.some(node => node.ref === newCustomNode(index).ref)) index++;
    const node = newCustomNode(index, kind);
    if (kind === 'expression' && node.editor) node.editor = { ...node.editor, left: definition.nodes[0]?.ref ?? 'A', right: definition.nodes[1]?.ref ?? definition.nodes[0]?.ref ?? 'A' };
    if (kind === 'sql' && node.editor && ['line', 'area'].includes(panel.type)) node.editor.shape = 'series';
    update({ query: { ...panel.query!, custom: { ...definition, nodes: [...definition.nodes, node] } } });
  };

  return <div className="flex min-h-0 flex-col gap-3 p-3">
    <div className="flex flex-wrap items-center gap-2">
      <Button icon={<ArrowLeft className="size-4" />} onClick={back}>{t('common.cancel')}</Button>
      <Input className="w-64" aria-label={t('designer.title')} value={panel.title} onChange={event => update({ title: event.target.value })} />
      <TimeRangePicker allowLive={false} value={range} onChange={setRange} />
      {panelId && <Button icon={<Eye className="size-4" />} onClick={() => navigate(`/dashboards/${id}/panels/${encodeURIComponent(panelId)}/preview?${params}`)}>{t('dashboard.preview')}</Button>}
      <Button icon={<Play className="size-4" />} loading={running} onClick={() => void run()}>{t('dashboard.run')}</Button>
      <Button variant="primary" icon={<Save className="size-4" />} disabled={running || executed !== key || Boolean(error || data?.error) || Object.values(parameterErrors).some(Boolean) || Boolean(compilationError || validateCustomPanel(compiledPanel!))} onClick={apply}>{t('designer.apply')}</Button>
      {data && executed !== key && <span className="text-warning">{t('designer.stale')}</span>}
    </div>
    {error && <p role="alert" className="text-error">{error}</p>}
    <div className="grid min-w-0 gap-3 lg:grid-cols-[minmax(0,1fr)_280px]">
      <div className="h-80 min-w-0 rounded border border-border bg-surface p-3"><PanelCardBody config={panel} data={data} loading={running} /></div>
      <div className="flex flex-col gap-3 rounded border border-border bg-surface p-3">
        <Field label={t('designer.type')}><Select value={panel.type} onValueChange={value => {
          const shape = ['line', 'area'].includes(value) ? 'series' : value === 'stat' ? 'scalar' : value === 'table' ? null : 'category';
          const nodes = definition.nodes.map(node => node.kind === 'sql' && node.editor?.mode === 'builder' && shape
            ? { ...node, editor: { ...node.editor, shape: shape as CustomPanelNode['shape'], groupBy: shape === 'scalar' ? [] : node.editor.groupBy } } : node);
          update({ type: value as PanelType, query: { ...panel.query!, custom: { ...definition, nodes } }, visualization: {
            version: 1, kind: value === 'stat' ? 'number' : ['line', 'area'].includes(value) ? 'time_series' : value as 'bar' | 'pie' | 'table',
            display: value === 'bar' ? 'bar' : 'line', range, outputs: definition.outputs.map(ref => ({ id: ref, alias: ref })),
          } });
        }} options={['line', 'area', 'bar', 'stat', 'table', 'pie'].map(value => ({ value, label: t('designer.types.' + value) }))} /></Field>
        <Field label={t('designer.unit')}><Input value={panel.unit ?? ''} onChange={event => update({ unit: event.target.value })} /></Field>
        <Checkbox checked={panel.showLegend !== false} onChange={event => update({ showLegend: event.target.checked })} label={t('designer.legend')} />
        <Field label={t('designer.outputs')}><div className="flex flex-wrap gap-2">{definition.nodes.map(node => <Checkbox key={node.ref} checked={definition.outputs.includes(node.ref)} label={node.ref + (node.alias ? ' · ' + node.alias : '')} onChange={event => {
          const outputs = event.target.checked ? [...definition.outputs, node.ref] : definition.outputs.filter(ref => ref !== node.ref);
          update({ query: { ...panel.query!, custom: { ...definition, outputs } }, visualization: panel.visualization && { ...panel.visualization, outputs: outputs.map(ref => ({ id: ref, alias: ref })) } });
        }} />)}</div></Field>
        <p className="text-xs text-muted">{t('designer.inputOnly')}</p>
        <Button variant="link" size="sm" icon={<Plus className="size-3.5" />} onClick={() => {
          const outputs = definition.nodes.map(node => node.ref);
          update({ query: { ...panel.query!, custom: { ...definition, outputs } }, visualization: panel.visualization && { ...panel.visualization, outputs: outputs.map(ref => ({ id: ref, alias: definition.nodes.find(node => node.ref === ref)?.alias || ref })) } });
        }}>{t('designer.overlay')}</Button>
        {['stat', 'bar', 'pie'].includes(panel.type) && <Button icon={<Plus className="size-4" />} onClick={() => setDisplayOpen(true)}>{t('charts.displaySettings')}</Button>}
        {panel.type === 'table' && panel.visualization && <TableSettings value={panel.visualization.tableSettings ?? {}} columns={data?.columns ?? []} onChange={tableSettings => update({ visualization: { ...panel.visualization!, tableSettings } })} />}
        <p className="text-xs text-muted">{t('designer.alignment')}</p>
      </div>
    </div>
    <div className="flex gap-2">
      <Button icon={<Plus className="size-4" />} disabled={definition.nodes.filter(n => n.kind === 'sql').length >= 12} onClick={() => add('sql')}>{t('designer.addSql')}</Button>
      <Button icon={<Plus className="size-4" />} disabled={definition.nodes.filter(n => n.kind === 'expression').length >= 8} onClick={() => add('expression')}>{t('designer.addExpression')}</Button>
    </div>
    {definition.nodes.map((node, index) => {
      const diagnostic = data?.nodes?.find(item => item.ref === node.ref);
      const columns = diagnostic?.columns ?? [];
      return <section key={node.ref} ref={element => { nodeElements.current[node.ref] = element; }} onFocusCapture={() => { if (focusedNode !== node.ref) setPosition('node', node.ref); }} className="flex min-w-0 flex-col gap-3 rounded border border-border bg-surface p-3">
        <div className="flex items-center gap-3"><span className="font-mono font-semibold">{node.ref} · {t(node.kind === 'sql' && node.editor ? 'designer.visualQuery' : 'designer.' + node.kind)}</span>
          {diagnostic && <span className="text-xs text-muted">{diagnostic.elapsedMs} ms{diagnostic.truncated ? ' · ' + t('designer.truncated') : ''}</span>}
          <Popconfirm title={t('common.confirmDelete')} onConfirm={() => {
            setParameterErrors(previous => ({ ...previous, [node.ref]: false }));
            update({ query: { ...panel.query!, custom: { ...definition, nodes: definition.nodes.filter(n => n.ref !== node.ref), outputs: definition.outputs.filter(ref => ref !== node.ref) } } });
          }}>
            <Button variant="link" size="sm" danger icon={<Trash2 className="size-4" />}>{t('common.delete')}</Button>
          </Popconfirm>
        </div>
        <Button variant="link" size="sm" icon={<Plus className="size-3.5" />} aria-expanded={!collapsed[node.ref]} onClick={() => setCollapsed(previous => ({ ...previous, [node.ref]: !previous[node.ref] }))}>{t(collapsed[node.ref] ? 'designer.expand' : 'designer.collapse')}</Button>
        <div hidden={collapsed[node.ref]}>
        <Field label={t('designer.alias')}><Input aria-label={t('designer.alias') + ' ' + node.ref} value={node.alias ?? ''} maxLength={128} onChange={event => updateNode(node.ref, { alias: event.target.value })} /></Field>
        <CustomNodeEditor node={node} preceding={definition.nodes.slice(0, index)} range={range} columns={columns}
          onChange={patch => updateNode(node.ref, patch)} onRun={() => void run()}
          onInvalid={invalid => setParameterErrors(previous => ({ ...previous, [node.ref]: invalid }))} />
        </div>
        {diagnostic && <details><summary className="cursor-pointer text-xs text-muted">{t('designer.result')}</summary><div className="max-h-48 overflow-auto"><table className="w-full text-xs"><thead><tr>{columns.map(column => <th key={column} className="p-1 text-left">{column}</th>)}</tr></thead><tbody>{diagnostic.rows.slice(0, 30).map((row, i) => <tr key={i}>{row.map((value, j) => <td key={j} className="p-1 font-mono">{value == null ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value)}</td>)}</tr>)}</tbody></table></div></details>}
      </section>;
    })}
    {displayOpen && ['bar', 'pie'].includes(panel.type) && <BarDisplayModal settings={(panel.type === 'pie' ? panel.visualization?.pieSettings : panel.visualization?.barSettings) ?? { format: DEFAULT_VALUE_FORMAT }} onClose={() => setDisplayOpen(false)} onApply={settings => update({ visualization: { ...panel.visualization!, [panel.type === 'pie' ? 'pieSettings' : 'barSettings']: settings } })} />}
    {displayOpen && panel.type === 'stat' && <NumberSettingsDrawer settings={panel.visualization?.numberSettings ?? DEFAULT_NUMBER_SETTINGS} onClose={() => setDisplayOpen(false)} onApply={numberSettings => update({ visualization: { ...panel.visualization!, numberSettings: { ...numberSettings, background: 'none' } } })} />}
    <ConfirmModal open={blocker.state === 'blocked'} onOpenChange={open => { if (!open && blocker.state === 'blocked') blocker.reset(); }} title={t('dashboard.unsaved')} content={t('dashboard.discardHint')} okText={t('dashboard.discard')} cancelText={t('common.cancel')} onOk={() => { if (blocker.state === 'blocked') blocker.proceed(); }} />
  </div>;
}
