import { describe, expect, it } from 'vitest';
import { luceneQuery, parseParameters, parseWorkbench, quoteIdentifier } from '../workbench';

describe('Analytics query builder', () => {
  it('binds nested boolean expressions and IoT values', () => {
    const query = luceneQuery('iot', '(signal:temp OR signal:humidity) AND NOT quality:0');
    expect(query.sql).toContain('{{iot}}');
    expect(query.sql).toContain('OR');
    expect(query.sql).toContain('NOT');
    expect(query.sql).not.toContain('humidity');
    expect(Object.values(query.parameters)).toEqual(['temp', 'humidity', 0]);
  });
  it('binds JSON keys containing colons and hostile values', () => {
    const query = luceneQuery('logs', 'attributes["time:stamp"]:"x\' OR 1=1"');
    expect(query.sql).not.toContain('time:stamp');
    expect(query.sql).not.toContain('1=1');
    expect(Object.values(query.parameters)).toContain('x\' OR 1=1');
  });
  it('preserves existing parameter allocation', () => {
    expect(parseParameters('{"value":42}')).toEqual({ value: 42 });
    expect(() => parseParameters('{"bad":[] }')).toThrow();
    expect(() => parseParameters('{"Bad":1}')).toThrow();
  });
  it('rejects invalid saved versions and limits', () => {
    expect(() => parseWorkbench('{"version":2}')).toThrow();
    expect(() => luceneQuery('iot', 'signal:x AND')).toThrow();
  });
  it('quotes catalog identifiers', () => {
    expect(quoteIdentifier('a"b')).toBe('"a""b"');
  });
});
