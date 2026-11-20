import { describe, expect, it } from 'vitest';
import { buildPatternSql, patternExpression, patternRows, patternsCsv, PatternExpressionError } from '../patterns';

describe('patterns query', () => {
  it('aggregates complete window with bound masks and a representative event', () => {
    const parameters = { p0: "api' OR true" };
    const sql = buildPatternSql({ domain: 'logs', where: 'service = $p0', parameters });
    expect(sql).toContain('ts >= $start AND ts < $end');
    expect(sql).toContain('GROUP BY pattern, level');
    expect(sql).toContain('arg_max(sample, ts)');
    expect(sql).toContain('total_patterns');
    expect(sql).toContain('LIMIT 200');
    expect(sql).not.toContain(parameters.p0);
    expect(Object.values(parameters)).toContain('<*>');
    expect(sql).not.toContain('LIMIT 500');
  });
  it('only accepts allowlisted fields, scalar functions and bound JSON paths', () => {
    const parameters = {};
    expect(patternExpression('lower(trim(body))', 'logs', parameters)).toBe('lower(trim("body"))');
    expect(patternExpression('attributes["message"]', 'logs', parameters)).toContain('json_extract_string');
    expect(Object.values(parameters)).toContain('$."message"');
    for (const value of ['body); SELECT 1', 'read_text(body)', 'unknown', 'body || service']) {
      expect(() => patternExpression(value, 'logs', {})).toThrow(PatternExpressionError);
    }
  });
  it('uses source-specific defaults and does not expose JSON for unsupported sources', () => {
    expect(patternExpression('', 'traces', {})).toBe('"name"');
    expect(patternExpression('', 'metrics', {})).toBe('"name"');
    expect(patternExpression('', 'sessions', {})).toBe('"domain"');
    expect(() => patternExpression('attributes.x', 'metrics', {})).toThrow();
  });
  it('decodes sparse trends and malformed samples safely', () => {
    const rows = patternRows({ columns: ['pattern', 'level', 'count', 'sample', 'trend'], rows: [
      ['订单 <*>', 'info', 2, '{"body":"订单 12"}', '[{"bucket":2,"count":2}]'],
      ['bad', '', 1, 'invalid', 'invalid'],
    ], elapsedMs: 1, truncated: false });
    expect(rows[0].trend).toHaveLength(30);
    expect(rows[0].trend.reduce((sum, value) => sum + value, 0)).toBe(2);
    expect(rows[0].sample?.body).toBe('订单 12');
    expect(rows[1].sample).toBeNull();
    expect(patternsCsv([{ ...rows[0], pattern: '=HYPERLINK("x")' }])).toContain('\'=');
  });
});
