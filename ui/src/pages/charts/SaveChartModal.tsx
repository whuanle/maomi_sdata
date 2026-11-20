import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Save } from 'lucide-react';
import { queryDashboards, getDashboard, saveDashboard } from '@/api/dashboards';
import type { DashboardItem, SearchSqlParameters } from '@/api/types';
import { Button, Modal, Select, toast } from '@/design-system';
import { appendPanel, normalizeDashboard } from '@/pages/dashboards/dashboardLayout';
import type { ChartSeries, ChartSource, ChartType } from './buildChartSql';
import { chartToPanel } from './chartPanel';
import { useChartWorkspace } from './ChartWorkspaceContext';

interface BaseProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  name: string;
}

type Props = BaseProps & QueryProps;

interface QueryProps {
  editor?: import('@/api/chartTypes').ChartEditorDefinition;
  numberSettings?: import('./numberTypes').NumberDisplaySettings;
  numberFormats?: Record<string, import('./numberTypes').NumberFormat>;
  numberTrend?: import('./numberTypes').NumberTrendQuery;
  pieSettings?: import('@/api/chartTypes').PieDisplaySettings;
  barSettings?: import('@/api/chartTypes').BarDisplaySettings;
  barGroupBy?: string[];
  orderBy?: string;
  tableSettings?: import('@/api/chartTypes').TableDisplaySettings;
  tableGroupBy?: string[];
  tableQuery?: import('@/api/chartTypes').TableQuerySettings;
  tableColumns?: import('@/api/chartTypes').TableColumnIdentity[];
  heatmap?: import('@/api/chartTypes').HeatmapConfig;
  sql: string;
  parameters: SearchSqlParameters;
  chartType: ChartType;
  source: ChartSource;
  series: ChartSeries[];
  range: { from: string; to: string };
  display: 'line' | 'bar';
  showLegend: boolean;
  autoGranularity: boolean;
  formulas?: import('@/api/chartTypes').ChartFormula[];
  showInputSeries?: boolean;
}

/** 只追加已运行图表，保存前重新读取目标并校验版本. */
export function SaveChartModal(props: Props) {
  const { t } = useTranslation();
  const workspace = useChartWorkspace();
  const [dashboards, setDashboards] = useState<DashboardItem[]>([]);
  const [selected, setSelected] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const title = props.name.trim() || t('charts.untitledChart');
  const autoGranularity = props.chartType === 'bar' ? false : props.autoGranularity;
  const panelInput = () => {
    const input = { ...props };
    Reflect.deleteProperty(input, 'open');
    Reflect.deleteProperty(input, 'onOpenChange');
    return { ...input, name: title, autoGranularity };
  };

  const applyRef = useRef<(() => void) | null>(null);
  applyRef.current = workspace.onApply ? () => workspace.onApply?.(chartToPanel(panelInput(), workspace.initial)) : null;
  const registerApply = workspace.onApplyActionChange;
  const canApply = Boolean(workspace.onApply);
  useEffect(() => {
    if (!canApply || !registerApply) return;
    registerApply(() => applyRef.current?.());
    return () => registerApply(null);
  }, [canApply, registerApply]);

  useEffect(() => {
    if (workspace.onApply) return;
    let active = true;
    void (async () => {
      try {
        const items: DashboardItem[] = [];
        for (let page = 1; ; page++) {
          const result = await queryDashboards({ page, pageSize: 200, kind: 'view' });
          items.push(...result.data);
          if (items.length >= result.count || result.data.length === 0) break;
        }
        if (active) setDashboards(items);
      } catch (err) {
        if (active) setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => { active = false; };
  }, []);

  const appliedOpen = useRef(false);
  useEffect(() => {
    if (!props.open) { appliedOpen.current = false; return; }
    if (canApply && registerApply && !appliedOpen.current) {
      appliedOpen.current = true;
      applyRef.current?.();
      props.onOpenChange(false);
    }
  }, [props.open, canApply, registerApply, props.onOpenChange]);

  const save = async () => {
    if (!selected || saving) return;
    setSaving(true);
    setError('');
    try {
      const dashboard = await getDashboard(Number(selected));
      const panel = chartToPanel(panelInput());
      const config = appendPanel(normalizeDashboard(dashboard.configJson), panel);
      await saveDashboard({ ...dashboard, configJson: JSON.stringify(config), expectedConfigJson: dashboard.configJson });
      toast.success(t('charts.savedToDashboard'));
      props.onOpenChange(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };

  if (workspace.onApply && registerApply) return null;
  if (workspace.onApply) return <Modal open={props.open} onOpenChange={props.onOpenChange} title={t('charts.applyPanel')} footer={<>
    <Button onClick={() => props.onOpenChange(false)}>{t('common.cancel')}</Button>
    <Button variant="primary" icon={<Save className="size-3.5" />} onClick={() => {
      workspace.onApply?.(chartToPanel(panelInput(), workspace.initial));
      props.onOpenChange(false);
    }}>{t('charts.applyPanel')}</Button>
  </>}><p className="text-muted">{t('charts.applyPanelHint')}</p><p className="mt-3 font-mono">{title}</p></Modal>;

  return <Modal open={props.open} onOpenChange={props.onOpenChange} title={t('charts.saveToDashboard')} footer={<>
    <Button onClick={() => props.onOpenChange(false)}>{t('common.cancel')}</Button>
    <Button variant="primary" icon={<Save className="size-3.5" />} disabled={loading || saving || !selected} onClick={() => void save()}>{saving ? t('common.loading') : t('charts.saveToDashboard')}</Button>
  </>}>
    <div className="mb-4 space-y-2 rounded border border-border p-3 font-mono text-xs">
      <div>{t('charts.chartPreview')}</div>
      <div><span className="text-muted">{t('charts.chartName')}: </span>{title}</div>
      <div><span className="text-muted">{t('charts.chartType')}: </span>{props.chartType}</div>
    </div>
    <label className="mb-2 block text-sm">{t('charts.dashboardRequired')}</label>
    <Select value={selected} onValueChange={setSelected} aria-label={t('charts.dashboardRequired')} disabled={loading || saving} placeholder={loading ? t('common.loading') : t('charts.selectDashboard')} options={dashboards.map((item) => ({ value: String(item.id), label: item.name }))} />
    {!loading && dashboards.length === 0 && <p className="mt-2 text-sm text-muted">{t('charts.noDashboards')}</p>}
    {error && <p role="alert" className="mt-2 text-sm text-error">{error}</p>}
  </Modal>;
}
