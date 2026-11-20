import type { ChartEditorDefinition, ExplorerConfig, PanelVisualization } from '@/api/chartTypes';
import type { PanelConfig } from '@/api/types';
import { seriesLabel } from './buildChartSql';
import { tableMetricNames } from './tableQuery';

const kinds = ['time_series', 'number', 'bar', 'pie', 'table', 'search', 'patterns', 'heatmap'];

/** 旧 Explorer 只在兼容入口读取，运行时不解释 Builder 或公式. */
export function visualizationFromExplorer(config: ExplorerConfig): PanelVisualization {
  const formulas = config.formulas ?? [];
  const hiddenColumns = config.chartType === 'table' && config.tableSettings && formulas.length && config.showInputSeries === false
    ? tableMetricNames({ seriesList: config.series, formulas, groupBy: config.tableGroupBy ?? [] }).slice(0, config.series.length) : [];
  return {
    version: 1, kind: config.chartType as PanelVisualization['kind'], display: config.display, range: config.range,
    outputs: [...config.series.map((item) => ({ id: item.id, alias: item.alias.trim(), label: seriesLabel(item), format: item.format })),
      ...formulas.map((item) => ({ id: item.id, alias: item.alias.trim() || item.expression }))],
    showInputSeries: !formulas.length || config.showInputSeries !== false, hiddenColumns,
    numberSettings: config.numberSettings, numberFormats: config.numberFormats,
    barSettings: config.barSettings, pieSettings: config.pieSettings,
    tableSettings: config.tableSettings, tableColumns: config.tableColumns, heatmap: config.heatmap,
  };
}

export function panelVisualizationUnsupported(panel?: PanelConfig): boolean {
  const visual = panel?.visualization;
  return Boolean(panel?.query?.kind === 'custom' && panel.query.custom?.version !== 1) || Boolean(visual && (visual.version !== 1 || !kinds.includes(visual.kind) || !Array.isArray(visual.outputs) || !visual.range));
}

export function panelDefinitionUnsupported(panel?: PanelConfig): boolean {
  const editor = panel?.editor ?? panel?.explorer?.editor;
  return panelVisualizationUnsupported(panel) || Boolean(editor && (editor.version !== 1
    || !Array.isArray(editor.series) || !Array.isArray(editor.formulas) || !Array.isArray(editor.groupBy)));
}

export function resolvePanelVisualization(panel?: PanelConfig): PanelVisualization | undefined {
  if (!panel || panelVisualizationUnsupported(panel)) return undefined;
  if (panel.visualization) return panel.visualization;
  const legacy = panel.explorer;
  if (!legacy || (panel.type === 'stat' && (legacy.version ?? 0) < 2)) return undefined;
  return visualizationFromExplorer(legacy);
}

export function resolvePanelTrend(panel?: PanelConfig) {
  return panel?.query?.numberTrend ?? (!panel?.visualization ? panel?.explorer?.numberTrend : undefined);
}

/** 编辑恢复可使用查询输入，但面板展示只消费输出身份. */
export function panelEditorSettings(panel?: PanelConfig): Partial<ExplorerConfig> | undefined {
  if (!panel) return undefined;
  if (!panel.visualization) return panel.explorer;
  const visual = resolvePanelVisualization(panel);
  if (!visual) return undefined;
  const editor = panel.editor;
  return { ...visual, chartType: visual.kind, series: editor?.series ?? [],
    formulas: editor?.formulas, source: editor?.source,
    barSettings: editor?.barSettings ?? visual.barSettings, barGroupBy: editor?.barGroupBy,
    orderBy: editor?.orderBy, heatmap: editor?.heatmap ?? visual.heatmap,
    tableQuery: editor?.tableQuery, tableGroupBy: editor?.groupBy, numberTrend: resolvePanelTrend(panel) };
}

export function panelQueryIdentity(panel: PanelConfig): string {
  const query = panel.query ? { ...panel.query, numberTrend: undefined } : panel.query;
  if (query?.custom) query.custom = { ...query.custom, nodes: query.custom.nodes.map(node => ({ ...node, editor: undefined, alias: undefined })) };
  return JSON.stringify([panel.id, query, panel.sql, panel.queryId, panel.rowLimit]);
}

export function editorWithSettings(editor: ChartEditorDefinition | undefined, config: ExplorerConfig): ChartEditorDefinition | undefined {
  return editor && { ...editor, barSettings: config.barSettings, barGroupBy: config.barGroupBy,
    orderBy: config.orderBy ?? editor.orderBy, heatmap: config.heatmap };
}
