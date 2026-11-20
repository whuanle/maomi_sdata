import { describe, expect, it } from 'vitest';
import type { ChartSeries } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { compileChartWhere } from '../chartWhere';
import { buildChartSql } from '../buildChartSql';

const series: ChartSeries = { id: 'a', aggFunc: 'count', field: '', where: '', alias: '' };
describe('chart WHERE compilation', () => {
  it('keeps old SQL conditions and raw drafts unchanged', () => {
    const draft = { ...series, where: 'severity = $severity' };
    const parameters = { severity: 'ERROR' };
    expect(compileChartWhere('logs', [draft], [], parameters)[0].where).toBe('(severity = $severity)');
    expect(draft.where).toBe('severity = $severity');
  });
  it('binds SQL literal values and rejects unbound parameters or statement boundaries', () => {
    const parameters: SearchSqlParameters = {};
    const result = compileChartWhere('logs', [{ ...series, where: "severity = 'ERR''OR' AND service IN ('api', 'worker') AND ts >= $start AND 42 > 0" }], [], parameters);
    expect(Object.values(parameters)).toEqual(expect.arrayContaining(["ERR'OR", 'api', 'worker', 42, 0]));
    expect(result[0].where).not.toContain('worker');
    expect(result[0].where).toContain('$start');
    expect(() => compileChartWhere('logs', [{ ...series, where: 'service = $missing' }], [], {})).toThrow();
    expect(() => compileChartWhere('logs', [{ ...series, where: 'true; SELECT 1' }], [], {})).toThrow();
  });
  it('isolates multiple Lucene values, JSON paths, filters and formula parameters', () => {
    const parameters: SearchSqlParameters = { p1: 'reserved', jp2: 'reserved-path' };
    const drafts: ChartSeries[] = [
      { ...series, whereSyntax: 'lucene', where: 'service:api attributes["http"]["method"]:POST' },
      { ...series, id: 'b', whereSyntax: 'lucene', where: 'service:worker attributes["http"]["status"]:500' },
    ];
    const compiled = compileChartWhere('logs', drafts, [{ field: 'severity', value: 'ERROR' }], parameters);
    const sql = buildChartSql({ domain: 'logs', chartType: 'number', seriesList: compiled, groupBy: [], granularity: 'auto', rangeMs: 3600000, parameters, formulas: [{ id: 'f', alias: 'ratio', expression: 'A / B * 100' }] });
    expect(parameters.p1).toBe('reserved');
    expect(parameters.jp2).toBe('reserved-path');
    expect(Object.values(parameters)).toEqual(expect.arrayContaining(['api', 'worker', 'POST', '500', 'ERROR', '$."http"."method"', '$."http"."status"', 100]));
    expect(sql).not.toContain('POST');
    expect(sql).not.toContain('worker');
    expect(drafts[0].where).toBe('service:api attributes["http"]["method"]:POST');
  });
  it.each([['logs', 'severity:ERROR'], ['traces', 'duration_us:>100'], ['metrics', 'name:cpu'], ['sessions', 'domain:example.org']] as const)('compiles %s conditions', (domain, where) => {
    const parameters = {};
    expect(compileChartWhere(domain, [{ ...series, whereSyntax: 'lucene', where }], [], parameters)[0].where).toContain('$p0');
    expect(Object.keys(parameters)).toHaveLength(1);
  });
  it('rejects invalid Lucene rather than passing it to DuckDB', () => {
    expect(() => compileChartWhere('logs', [{ ...series, whereSyntax: 'lucene', where: 'unknown:value' }], [], {})).toThrow();
  });
});
