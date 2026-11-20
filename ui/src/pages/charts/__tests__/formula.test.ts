import { describe, expect, it } from 'vitest';
import { bindChartParameter, formulaSql, parseFormula, seriesRef } from '../formula';
import { buildChartSql } from '../buildChartSql';
import type { SearchSqlParameters } from '@/api/types';

describe('formula parser', () => {
  it('preserves arithmetic precedence, parentheses and unary signs', () => {
    const parameters: SearchSqlParameters = {};
    const node = parseFormula('-A / (A + B) * 100', ['A', 'B']);
    expect(formulaSql(node, ['A', 'B'], (n) => bindChartParameter(parameters, n))).toBe('(((0 - v0) / NULLIF((v0 + v1), 0)) * CAST($cf0 AS DOUBLE))');
    expect(parameters).toEqual({ cf0: 100 });
    expect(seriesRef(26)).toBe('AA');
  });
  it.each(['', 'A +', '(A+B', 'A B', 'A.foo', 'abs(A)', 'A; SELECT 1', 'A**B', '1e999', 'A = B', 'A / C'])('rejects invalid expression %s', (expression) => {
    expect(() => parseFormula(expression, ['A', 'B'])).toThrow();
  });
  it('bounds length, nesting and tokens', () => {
    expect(() => parseFormula('('.repeat(30) + 'A' + ')'.repeat(30), ['A'])).toThrow();
    expect(() => parseFormula('A+'.repeat(80) + 'A', ['A'])).toThrow();
    expect(() => parseFormula(' '.repeat(513) + 'A', ['A'])).toThrow();
  });
  it('does not retarget a deleted reference', () => {
    expect(() => parseFormula('B / A', ['A', 'C'])).toThrow('formulaUnknown');
  });
  it('binds literals and aliases, aligns by the complete group tuple', () => {
    const parameters: SearchSqlParameters = { p0: "error'quoted", p1: 'api' };
    const sql = buildChartSql({
      domain: 'logs', chartType: 'time_series', granularity: 'auto', rangeMs: 3600000, groupBy: ['service', 'severity'], parameters,
      seriesList: [
        { id: 's1', ref: 'A', aggFunc: 'count', field: '', where: 'severity = $p0', alias: "Alias'quoted" },
        { id: 's2', ref: 'B', aggFunc: 'count', field: '', where: 'service = $p1', alias: '' },
      ],
      formulas: [{ id: 'f1', expression: 'A / (A + B) * 100', alias: 'Ratio' }],
    });
    expect(sql).toContain('GROUP BY ts_bucket, "service", "severity"');
    expect(sql).toContain('v0 / NULLIF((v0 + v1), 0)');
    expect(sql).not.toContain("Alias'quoted");
    expect(Object.values(parameters)).toContain("Alias'quoted");
    expect(Object.values(parameters)).toContain(100);
    expect(sql).toContain('isfinite(value)');
    expect(sql).toContain('LIMIT 10001');
  });
});
