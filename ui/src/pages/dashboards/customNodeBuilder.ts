import type { CustomNodeEditor, CustomPanelNode, SearchSqlParameters } from '@/api/types';
import type { ChartSeries } from '@/api/chartTypes';
import { buildChartSql, resolveGranularity } from '@/pages/charts/buildChartSql';
import { compileChartWhere } from '@/pages/charts/chartWhere';
import type { TimeRange } from '@/design-system';

export function defaultNodeEditor(): CustomNodeEditor {
  return { version: 1, mode: 'builder', source: 'logs', shape: 'scalar', aggregation: 'count', field: '', where: '', groupBy: [], granularity: 'auto' };
}

/** 编辑草稿只在此边界编译，筛选和指标属性共用独立绑定参数空间. */
export function compileCustomNode(node: CustomPanelNode, range: TimeRange): CustomPanelNode {
  const editor = node.editor;
  if (!editor) return node;
  if (editor.version !== 1) throw new Error('designer.unsupported');
  if (editor.mode === 'guided') {
    const left = editor.left ?? '';
    const right = editor.right ?? '';
    const expression = editor.operation === 'reduce' ? left
      : editor.operation === 'percentage' ? `(${left} / ${right}) * 100`
      : `${left} ${editor.operation === 'sum' ? '+' : editor.operation === 'difference' ? '-' : '/'} ${right}`;
    return { ...node, expression };
  }
  const parameters: SearchSqlParameters = {};
  if (editor.source === 'metrics' && !editor.metricName) throw new Error('designer.chooseMetric');
  const series: ChartSeries = { id: node.ref, aggFunc: editor.aggregation, field: editor.field,
    alias: '', where: editor.where, whereSyntax: 'lucene', metricName: editor.metricName, metricFilters: editor.metricFilters };
  const compiled = compileChartWhere(editor.source, [series], [], parameters);
  const groupBy = ['category', 'series'].includes(editor.shape) ? editor.groupBy : [];
  const rangeMs = Date.parse(range.to) - Date.parse(range.from);
  const autoGranularity = editor.shape === 'series' && editor.granularity === 'auto';
  if (autoGranularity) {
    const granularity = resolveGranularity('auto', rangeMs);
    parameters.chart_bucket = ({ '1m': '1 minute', '5m': '5 minutes', '15m': '15 minutes', '30m': '30 minutes', '1h': '1 hour', '6h': '6 hours', '1d': '1 day' })[granularity];
  }
  const sql = buildChartSql({ domain: editor.source, chartType: editor.shape === 'table' ? 'search'
    : editor.shape === 'series' ? 'time_series' : editor.shape === 'category' ? 'pie' : 'number',
    seriesList: compiled, groupBy, granularity: editor.granularity, rangeMs, parameters, bucketParameter: autoGranularity });
  if (Object.keys(parameters).length > 64) throw new Error('designer.invalidParameters');
  return { ...node, sql, parameters, shape: editor.shape, valueField: 'value', timeField: editor.shape === 'series' ? 'ts_bucket' : '',
    groupFields: groupBy, autoGranularity };
}

export function compileCustomPanel(panel: import('@/api/types').PanelConfig, range: TimeRange) {
  if (!panel.query?.custom) return panel;
  return { ...panel, query: { ...panel.query, custom: { ...panel.query.custom,
    nodes: panel.query.custom.nodes.map(node => compileCustomNode(node, range)) } } };
}
