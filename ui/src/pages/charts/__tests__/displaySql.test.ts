import { describe, expect, it } from 'vitest';
import { displaySql } from '../displaySql';

describe('displaySql', () => {
  it('renders typed values and escapes strings without changing bindings', () => {
    const parameters = { p0: "猫咪 O'Brien", p1: 12.5, p2: true, p3: false, p4: null };
    expect(displaySql('SELECT $p0, $p1, $p2, $p3, $p4, $p0', parameters)).toBe("SELECT '猫咪 O''Brien', 12.5, TRUE, FALSE, NULL, '猫咪 O''Brien'");
    expect(parameters.p0).toBe("猫咪 O'Brien");
  });
  it('matches whole parameter names, leaves unknowns and time bounds intact', () => {
    expect(displaySql('SELECT $p1, $p10, $p100, $start, $end, $bucket, col$p1', { p1: 1, p10: 10, start: 'now', end: 'later', bucket: 60 }))
      .toBe('SELECT 1, 10, $p100, $start, $end, 60, col$p1');
  });
  it('does not interpret quoted strings, identifiers, comments or dollar strings', () => {
    const sql = `SELECT '$p0', 'it''s $p0', "$p0", $$ $p0 $$, $body$ $p0 $body$, $p0 -- $p0
/* $p0 */ + $p0`;
    expect(displaySql(sql, { p0: 'api' })).toBe(`SELECT '$p0', 'it''s $p0', "$p0", $$ $p0 $$, $body$ $p0 $body$, 'api' -- $p0
/* $p0 */ + 'api'`);
  });
  it('keeps non-finite and inherited parameters unresolved', () => {
    expect(displaySql('SELECT $p0, $toString', { p0: Infinity })).toBe('SELECT $p0, $toString');
  });
});
