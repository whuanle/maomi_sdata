import { describe, expect, it } from 'vitest';
import { chartToPanel, panelEditorDefinition, type ChartPanelInput } from '../chartPanel';

const input: ChartPanelInput = {
  name: 'Ratio', chartType: 'time_series', source: 'logs',
  series: [{ id: 'a', ref: 'A', aggFunc: 'count', field: '', where: 'severity:ERROR', whereSyntax: 'lucene', alias: 'Errors' }],
  formulas: [{ id: 'ratio', expression: 'A * 100', alias: 'Ratio' }],
  range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' },
  display: 'line', autoGranularity: false, showLegend: false,
  sql: 'SELECT $cf0 AS value', parameters: { cf0: 100 },
};
const editor = { version: 1 as const, mode: 'builder' as const, chartType: input.chartType, source: input.source,
  series: input.series, formulas: input.formulas!, groupBy: ['service'], granularity: '5m' as const };

describe('chart panel round trip', () => {
  it('separates execution, visualization and editor with a single compatibility mirror', () => {
    const panel = chartToPanel({ ...input, editor });
    expect(panel.editor).toEqual(panel.explorer?.editor);
    expect(panel.visualization).toMatchObject({ version: 1, kind: 'time_series', outputs: [{ id: 'a', alias: 'Errors' }, { id: 'ratio', alias: 'Ratio' }] });
    expect(panel.visualization).not.toHaveProperty('formulas');
    expect(panel.visualization).not.toHaveProperty('series');
    expect(panel.query).toMatchObject({ sql: input.sql, parameters: input.parameters });
    expect(panelEditorDefinition({ ...panel, explorer: undefined })).toEqual(editor);
  });
  it('does not send stale editor definitions into non-SQL builders', () => {
    const panel = chartToPanel({ ...input, editor });
    expect(panelEditorDefinition({ ...panel, query: { kind: 'metric', name: 'cpu' } })).toBeUndefined();
  });
  it('saves trend SQL in the query and preserves Bar and Heatmap query inputs', () => {
    const trend = { sql: 'SELECT $x AS value', parameters: { x: 1 } };
    const panel = chartToPanel({ ...input, editor, numberTrend: trend, heatmap: { field: 'duration_us', bins: 40, scale: 'log' } });
    expect(panel.query?.numberTrend).toEqual(trend);
    expect(panel.visualization).not.toHaveProperty('numberTrend');
    expect(panel.editor?.heatmap).toEqual(panel.visualization?.heatmap);
  });
  it('preserves Number metric selection, Delta and structured attributes when reopened', () => {
    const series = [{ id: 'metric', ref: 'A', aggFunc: 'avg' as const, field: 'value', alias: 'CPU', where: '', metricName: "cpu'usage", metricDelta: true, metricFilters: [{ field: 'labels["host"]', value: 'node-1' }] }];
    const definition = { ...editor, chartType: 'number' as const, source: 'metrics' as const, series, formulas: [], groupBy: [] };
    const panel = chartToPanel({ ...input, chartType: 'number', source: 'metrics', series, editor: definition });
    expect(panelEditorDefinition(JSON.parse(JSON.stringify(panel)))).toEqual(definition);
  });
  it('preserves Bar sample filters when reopening the builder', () => {
    const definition = { ...editor, chartType: 'bar' as const, syntax: 'lucene' as const, valueFilters: [{ field: 'service', value: "api'quoted" }] };
    const panel = chartToPanel({ ...input, chartType: 'bar', editor: definition });
    expect(panelEditorDefinition(JSON.parse(JSON.stringify(panel)))?.valueFilters).toEqual(definition.valueFilters);
  });
  it('restores raw builder definition independently of compiled parameters', () => {
    const panel = chartToPanel({ ...input, editor });
    const stored = JSON.parse(JSON.stringify(panel));
    expect(panelEditorDefinition(stored)).toEqual(editor);
    expect(stored.query.parameters).toEqual({ cf0: 100 });
    expect(stored.explorer.editor.series[0].where).toBe('severity:ERROR');
  });
  it('keeps panel identity, geometry and unknown fields on update', () => {
    const original = { ...chartToPanel(input), id: 'stable', layout: { x: 6, y: 8, w: 6, h: 3 }, custom: 'retained' };
    const updated = chartToPanel({ ...input, editor }, original);
    expect(updated.id).toBe('stable');
    expect(updated.layout).toEqual(original.layout);
    expect(updated).toHaveProperty('custom', 'retained');
  });
  it('preserves legacy SQL visualization through repeated edits', () => {
    const original = { ...chartToPanel(input), type: 'area' as const };
    const definition = panelEditorDefinition(original)!;
    const updated = chartToPanel({ ...input, editor: definition }, original);
    expect(updated.type).toBe('area');
    expect(updated.explorer).toEqual(original.explorer);
    expect(panelEditorDefinition(updated)?.compatibility).toBe(true);
    const repeated = chartToPanel({ ...input, editor: panelEditorDefinition(updated) }, updated);
    expect(repeated.type).toBe('area');
    expect(repeated.visualization).toEqual(original.visualization);
  });
  it('refuses unsupported definitions without changing the original', () => {
    const original = { ...chartToPanel(input), editor: { ...editor, version: 99 } };
    const serialized = JSON.stringify(original);
    expect(() => chartToPanel(input, original as unknown as ReturnType<typeof chartToPanel>)).toThrow('charts.unsupportedPanelDefinition');
    expect(JSON.stringify(original)).toBe(serialized);
  });
  it('uses explicit SQL fallback without inventing a builder', () => {
    expect(panelEditorDefinition(chartToPanel(input))).toMatchObject({ mode: 'sql', formulas: [], groupBy: [] });
    expect(panelEditorDefinition({ id: 'm', title: 'm', type: 'markdown' })).toBeUndefined();
  });
});
