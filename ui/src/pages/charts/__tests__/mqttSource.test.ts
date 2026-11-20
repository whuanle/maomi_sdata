import { describe, expect, it } from 'vitest';
import { SEARCH_FIELDS } from '@/api/search';
import type { ChartEditorDefinition } from '@/api/chartTypes';
import { buildChartSql } from '../buildChartSql';
import { compileChartWhere } from '../chartWhere';
import { defaultGroup, defaultTableSeries } from '../tableBuilder';
import { buildEventSearch, EVENT_DEFAULT_FIELDS } from '../eventSearchQuery';
import { buildPatternSql } from '../patterns';
import { parseChartUrlState, serializeChartUrlState } from '../chartUrlState';
import { chartToPanel, panelEditorDefinition } from '../chartPanel';

const range = { from: '2026-11-19T00:00:00.000Z', to: '2026-11-19T01:00:00.000Z' };
const series = defaultTableSeries('iot');
const editor: ChartEditorDefinition = { version: 1, mode: 'builder', source: 'iot', chartType: 'time_series',
  series: [series], formulas: [], groupBy: ['device_key', 'signal'], granularity: '1m' };

describe('MQTT telemetry source', () => {
  it('uses the existing telemetry schema and numeric defaults', () => {
    expect(SEARCH_FIELDS.iot.map(item => item.field).sort()).toEqual(['ts', 'device_key', 'signal', 'num_value', 'str_value', 'quality'].sort());
    expect(series).toMatchObject({ aggFunc: 'avg', field: 'num_value' });
    expect(defaultGroup('iot')).toBe('device_key');
  });

  it.each(['time_series', 'number', 'table', 'bar', 'pie'] as const)('builds %s over the IoT dataset with bound filters', chartType => {
    const parameters = {};
    const compiled = compileChartWhere('iot', [{ ...series, where: 'device_key:"sensor-01" num_value:>20', whereSyntax: 'lucene' }], [], parameters);
    const sql = buildChartSql({ domain: 'iot', chartType, seriesList: compiled, groupBy: chartType === 'number' ? [] : ['device_key'],
      granularity: '1m', rangeMs: 3600000, parameters });
    expect(sql).toContain('{{iot}}');
    expect(sql).toContain('num_value');
    expect(sql).not.toContain('sensor-01');
    expect(Object.values(parameters)).toContain('sensor-01');
    expect(Object.values(parameters)).toContain(20);
  });

  it('supports string telemetry events and patterns with bound values', () => {
    const events = buildEventSearch({ domain: 'iot', fields: EVENT_DEFAULT_FIELDS.iot, syntax: 'lucene',
      where: 'running', filters: [{ field: 'str_value', value: "RUN'quoted" }], orderBy: 'ts DESC' });
    expect(events.sql).toContain('{{iot}}');
    expect(events.sql).toContain('str_value LIKE');
    expect(events.sql).not.toContain("RUN'quoted");
    expect(Object.values(events.parameters)).toContain("RUN'quoted");
    expect(buildPatternSql({ domain: 'iot', parameters: {} })).toContain('CAST("str_value" AS VARCHAR)');
  });

  it('restores MQTT source through URLs and persisted panels', () => {
    const input = { editor, range };
    expect(parseChartUrlState(new URLSearchParams(serializeChartUrlState(input))).state).toEqual(input);
    const panel = chartToPanel({ name: 'Telemetry', source: 'iot', chartType: 'time_series', series: [series], editor,
      range, display: 'line', autoGranularity: false, showLegend: true,
      sql: 'SELECT avg(num_value) AS value FROM {{iot}} WHERE ts >= $start AND ts < $end', parameters: {} });
    expect(panelEditorDefinition(JSON.parse(JSON.stringify(panel)))).toEqual(editor);
    expect(panel.query?.sql).toContain('{{iot}}');
    expect(panelEditorDefinition({ ...panel, editor: undefined, explorer: { ...panel.explorer!, editor: undefined } })?.source).toBe('iot');
  });
});
