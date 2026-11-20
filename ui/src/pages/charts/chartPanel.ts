import type { ChartEditorDefinition, ChartType, ExplorerConfig } from '@/api/chartTypes';
import type { PanelConfig, PanelType, SearchSqlParameters } from '@/api/types';
import { editorWithSettings, panelDefinitionUnsupported, visualizationFromExplorer } from './panelDefinition';

export interface ChartPanelInput extends Omit<ExplorerConfig, 'chartType' | 'source'> {
  chartType: ChartType;
  source: import('@/api/chartTypes').ChartSource;
  name: string;
  sql: string;
  parameters: SearchSqlParameters;
  showLegend: boolean;
}

/** 页面保存与画布应用共用；更新时保留几何布局和扩展字段. */
export function chartToPanel(input: ChartPanelInput, original?: PanelConfig): PanelConfig {
  if (panelDefinitionUnsupported(original)) throw new Error('charts.unsupportedPanelDefinition');
  const { name, sql, parameters, showLegend, ...explorer } = input;
  const type: PanelType = input.chartType === 'number' ? 'stat'
    : input.chartType === 'time_series' ? input.display
    : input.chartType === 'search' || input.chartType === 'patterns' ? 'table' : input.chartType;
  const previousEditor = original?.editor ?? original?.explorer?.editor;
  const legacySql = Boolean(original && input.editor?.mode === 'sql' && (!previousEditor || previousEditor.compatibility));
  const editor = editorWithSettings(input.editor, explorer);
  if (editor && (input.editor?.compatibility || previousEditor?.compatibility)) editor.compatibility = true;
  return {
    ...original,
    id: original?.id ?? crypto.randomUUID(),
    title: name,
    type: legacySql && original ? original.type : type,
    showLegend,
    layout: original?.layout ?? { w: 6, h: 4 },
    rowLimit: input.chartType === 'table' ? input.tableQuery?.topN ?? 500 : 10000,
    sql: undefined, queryId: undefined,
    query: { kind: 'sql', sql, parameters: { ...parameters }, autoGranularity: input.autoGranularity, numberTrend: input.numberTrend },
    visualization: legacySql && original ? original.visualization : visualizationFromExplorer(explorer),
    editor,
    explorer: legacySql ? original?.explorer : { ...original?.explorer, ...explorer, editor, version: 3 },
  };
}

export function panelEditorDefinition(panel?: PanelConfig): ChartEditorDefinition | undefined {
  if (panelDefinitionUnsupported(panel) || panel?.type === 'markdown' || (panel?.query?.kind && panel.query.kind !== 'sql')) return undefined;
  const editor = panel?.editor ?? panel?.explorer?.editor;
  if (editor?.version === 1 && Array.isArray(editor.series) && Array.isArray(editor.formulas) && Array.isArray(editor.groupBy)) return editor;
  if (!panel || !(panel.query?.sql || panel.sql)) return undefined;
  const type = panel.visualization?.kind ?? panel.explorer?.chartType;
  const chartType: ChartType = type === 'bar' || type === 'pie' || type === 'table' ? type
    : panel.type === 'stat' ? 'number' : panel.type === 'table' ? 'table' : panel.type === 'pie' ? 'pie' : 'time_series';
  return {
    version: 1, compatibility: true, mode: 'sql', source: ['logs', 'traces', 'metrics', 'sessions', 'iot'].includes(panel.explorer?.source ?? '') ? panel.explorer!.source as ChartEditorDefinition['source'] : 'logs',
    chartType, series: panel.explorer?.series ?? [], formulas: [], groupBy: [], granularity: 'auto',
  };
}
