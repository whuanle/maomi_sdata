import { describe, expect, it } from 'vitest';
import { chartToPanel } from '../chartPanel';
import { panelEditorSettings, panelQueryIdentity, resolvePanelVisualization } from '../panelDefinition';
import { panelResult } from '../chartResult';

const input = {
  name: 'Table', chartType: 'table' as const, source: 'logs' as const,
  sql: 'SELECT 1', parameters: {}, series: [{ id: 'a', ref: 'A', aggFunc: 'count' as const, field: '', where: '', alias: 'Requests' }],
  formulas: [{ id: 'f', expression: 'A * 2', alias: 'Double' }],
  range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' },
  display: 'line' as const, autoGranularity: false, showLegend: true,
  showInputSeries: false, tableSettings: {}, tableGroupBy: [],
};

describe('panel definitions', () => {
  it('hides formula input columns without an editor or Explorer', () => {
    const panel = { ...chartToPanel(input), editor: undefined, explorer: undefined };
    const visual = resolvePanelVisualization(panel)!;
    expect(panelResult({ columns: ['Requests', 'Double'], rows: [[2, 4]] }, visual)).toEqual({ columns: ['Double'], rows: [[4]] });
  });
  it('prefers explicit visualization and restores independent editor settings', () => {
    const panel = chartToPanel({ ...input, editor: { version: 1, mode: 'builder', source: 'logs', chartType: 'table', series: input.series, formulas: input.formulas, groupBy: [], granularity: 'auto' } });
    panel.explorer!.tableSettings = { decimals: 8 };
    expect(resolvePanelVisualization(panel)?.tableSettings).toEqual({});
    expect(panelEditorSettings({ ...panel, explorer: undefined })?.series).toEqual(input.series);
  });
  it('query identity includes row limits but excludes display and editing changes', () => {
    const panel = chartToPanel(input);
    expect(panelQueryIdentity({ ...panel, title: 'Renamed', editor: undefined, explorer: undefined, visualization: undefined })).toBe(panelQueryIdentity(panel));
    expect(panelQueryIdentity({ ...panel, rowLimit: 10 })).not.toBe(panelQueryIdentity(panel));
    expect(panelQueryIdentity({ ...panel, query: { ...panel.query!, numberTrend: { sql: 'SELECT 2', parameters: {} } } })).toBe(panelQueryIdentity(panel));
  });
});
