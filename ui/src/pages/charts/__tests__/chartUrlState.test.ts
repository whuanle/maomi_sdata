import { describe, expect, it } from 'vitest';
import { CHART_URL_TABS, parseChartUrlState, serializeChartUrlState, type ChartUrlState } from '../chartUrlState';

const range = { from: '2026-11-18T00:00:00.000Z', to: '2026-11-18T01:00:00.000Z' };
function state(tab: ChartUrlState['editor']['chartType']): ChartUrlState {
  return {
    editor: { version: 1, chartType: tab, mode: 'builder', source: tab === 'heatmap' ? 'traces' : 'logs',
      series: [{ id: 'stable', ref: 'B', aggFunc: 'count', field: '', where: "service:中文", whereSyntax: 'lucene', alias: '名称 & +' }],
      formulas: [{ id: 'f', expression: 'B * 2', alias: '公式' }], groupBy: ['service'], granularity: 'auto',
      ...(tab === 'search' ? { eventSearch: { domain: 'logs', fields: 'ts, body', syntax: 'sql', where: 'body = $p0', orderBy: 'ts DESC', filters: [{ field: 'body', value: null, exists: true }] } } : {}),
    },
    range, sql: 'SELECT $p0 AS value', parameters: { p0: "中文 & + '", p1: 2, p2: true, p3: null },
    ...(tab === 'heatmap' ? { heatmap: { field: 'duration_us', bins: 20 } } : {}),
  };
}

describe('chartUrlState', () => {
  it.each(CHART_URL_TABS)('%s 查询条件往返不丢失', (tab) => {
    const input = state(tab);
    expect(parseChartUrlState(new URLSearchParams(serializeChartUrlState(input)))).toEqual({ tab, state: input });
  });
  it('相对时间仅保存预设并在恢复时重新锚定', () => {
    const input = { ...state('time_series'), range: { ...range, preset: '1h' } };
    const params = serializeChartUrlState(input);
    expect(JSON.parse(params.state).range).toEqual({ preset: '1h' });
    const restored = parseChartUrlState(new URLSearchParams(params)).state!;
    expect(restored.range.preset).toBe('1h');
    expect(new Date(restored.range.to).getTime() - new Date(restored.range.from).getTime()).toBe(3600000);
    expect(restored.range.to).not.toBe(range.to);
  });
  it('表格稳定标识、结构化筛选和排序保持一致', () => {
    const input = state('table');
    input.editor.tableSeries = [{ ...input.editor.series[0], filters: [{ id: 'filter', field: 'severity', operator: 'eq', value: 'ERROR' }], threshold: { operator: 'gte', value: '2' } }];
    input.editor.tableQuery = { topN: 20, sort: [{ columnId: 'stable', direction: 'desc' }] };
    expect(parseChartUrlState(new URLSearchParams(serializeChartUrlState(input))).state).toEqual(input);
  });
  it.each(['{', 'null', '{"version":2}', '{"version":1,"editor":{}}'])('非法状态整体回落：%s', (raw) => {
    expect(parseChartUrlState(new URLSearchParams({ tab: 'bar', state: raw }))).toEqual({ tab: 'bar' });
  });
  it('非法 tab 回落默认，非法字段不部分恢复', () => {
    expect(parseChartUrlState(new URLSearchParams({ tab: 'unknown' }))).toEqual({ tab: 'time_series' });
    const input = state('bar');
    const params = serializeChartUrlState(input);
    const raw = JSON.parse(params.state);
    raw.editor.series[0].aggFunc = 'unknown';
    expect(parseChartUrlState(new URLSearchParams({ ...params, state: JSON.stringify(raw) }))).toEqual({ tab: 'bar' });
  });
  it('热力图尺度往返并拒绝无效分桶', () => {
    const input = state('heatmap');
    input.heatmap = { field: 'duration_us', bins: 60, scale: 'log', colorScale: 'log' };
    expect(parseChartUrlState(new URLSearchParams(serializeChartUrlState(input))).state).toEqual(input);
    for (const heatmap of [{ ...input.heatmap, bins: 100 }, { ...input.heatmap, scale: 'invalid' }]) {
      const params = serializeChartUrlState(input);
      expect(parseChartUrlState(new URLSearchParams({ ...params, state: JSON.stringify({ version: 1, ...input, heatmap }) })).state).toBeUndefined();
    }
  });
  it('拒绝非法参数、时间与来源组合', () => {
    for (const patch of [
      { parameters: { p0: {} } }, { range: { from: range.to, to: range.from } },
      { editor: { ...state('heatmap').editor, source: 'logs' } },
    ]) {
      const input = state('heatmap');
      const params = serializeChartUrlState(input);
      expect(parseChartUrlState(new URLSearchParams({ ...params, state: JSON.stringify({ version: 1, ...input, ...patch }) }))).toEqual({ tab: 'heatmap' });
    }
  });
});
