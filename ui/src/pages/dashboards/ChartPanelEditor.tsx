import { useCallback, useState } from 'react';
import { Save, Eye } from 'lucide-react';
import { useSearchParams } from 'react-router';
import type { ChartType } from '@/api/chartTypes';
import { useTranslation } from 'react-i18next';
import type { PanelConfig } from '@/api/types';
import { Button, Modal, type TimeRange } from '@/design-system';
import { ChartWorkspace } from '@/pages/charts/ChartWorkspace';
import { ChartWorkspaceContext } from '@/pages/charts/ChartWorkspaceContext';
import { panelEditorDefinition } from '@/pages/charts/chartPanel';
import { panelDefinitionUnsupported } from '@/pages/charts/panelDefinition';
import { PanelEditorDrawer } from './PanelEditorDrawer';

/** 图表工作区只提交本地面板草稿，数据库保存归仪表盘所有. */
export function ChartPanelEditor({ initial, range, onClose, onApply, onPreview }: {
  initial?: PanelConfig;
  onPreview?: () => void;
  range: TimeRange;
  onClose: () => void;
  onApply: (panel: PanelConfig) => void;
}) {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab');
  const urlTab = ['time_series', 'number', 'bar', 'pie', 'table', 'search', 'patterns', 'heatmap'].includes(tab ?? '') ? tab as ChartType : undefined;
  const [applyAction, setApplyAction] = useState<(() => void) | null>(null);
  const registerApply = useCallback((action: (() => void) | null) => setApplyAction(() => action), []);
  const definition = panelEditorDefinition(initial);
  const advanced = Boolean(initial && !definition);
  if (panelDefinitionUnsupported(initial)) return <Modal open onOpenChange={(open) => { if (!open) onClose(); }} title={t('dashboard.editPanel')} footer={<Button onClick={onClose}>{t('common.cancel')}</Button>}><p role="alert" className="text-warning">{t('charts.unsupportedPanelDefinition')}</p></Modal>;
  if (advanced) return <PanelEditorDrawer open onPreview={onPreview} initial={initial ?? null} from={range.from} to={range.to} onOpenChange={(open) => { if (!open) onClose(); }} onSaved={(panel) => { onApply(panel); onClose(); }} />;
  return <Modal open onOpenChange={(open) => { if (!open) onClose(); }} title={t(initial ? 'dashboard.editPanel' : 'dashboard.addPanel')} width="96vw" footer={<>
    <Button onClick={onClose}>{t('common.cancel')}</Button>
    {onPreview && <Button icon={<Eye className="size-3.5" />} onClick={onPreview}>{t('dashboard.preview')}</Button>}
    <Button variant="primary" icon={<Save className="size-3.5" />} disabled={!applyAction} onClick={() => applyAction?.()}>{t('charts.applyPanel')}</Button>
  </>}>
    <p className="mb-3 text-xs text-muted">{t('charts.applyPanelHint')}</p>
    {initial && !initial.editor && !initial.explorer?.editor && <p role="status" className="mb-3 text-xs text-warning">{t('charts.legacySql')}</p>}
    <ChartWorkspaceContext.Provider value={{ mode: 'panel', urlTab, onTabChange: tab => setParams(previous => { const next = new URLSearchParams(previous); next.set('tab', tab); return next; }, { replace: true }), onApplyActionChange: registerApply, initial: initial, definition, range: { from: range.from, to: range.to }, onApply: (panel) => { onApply(panel); onClose(); } }}>
      <ChartWorkspace />
    </ChartWorkspaceContext.Provider>
  </Modal>;
}
