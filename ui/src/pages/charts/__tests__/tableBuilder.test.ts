import { describe, expect, it } from 'vitest';
import { compileTableSeries, defaultTableSeries, defaultGroup } from '../tableBuilder';
import type { SearchSqlParameters } from '@/api/types';

describe('Table guided builder', () => {
  it.each(['logs', 'traces', 'metrics', 'sessions'] as const)('provides valid defaults for %s', (source) => {
    const item = defaultTableSeries(source);
    expect(defaultGroup(source)).toBeTruthy();
    expect(compileTableSeries(source, [item], {})).toHaveLength(1);
  });
  it('binds distinct series values and aggregate thresholds in one parameter space', () => {
    const params: SearchSqlParameters = {};
    const items = compileTableSeries('logs', [
      { ...defaultTableSeries('logs'), filters: [{ id: 'a', field: 'service', operator: 'eq', value: "api'; DROP TABLE logs;--" }], threshold: { operator: 'gt', value: '10' } },
      { ...defaultTableSeries('logs'), filters: [{ id: 'b', field: 'severity', operator: 'eq', value: 'ERROR' }] },
    ], params);
    expect(items[0].where).not.toContain('DROP');
    expect(items[0].having).toContain('count(*) > $cf1');
    expect(items[1].where).toContain('$cf2');
    expect(Object.values(params)).toEqual(["api'; DROP TABLE logs;--", 10, 'ERROR']);
  });
  it('rejects unsupported fields, nonnumeric aggregates and invalid numbers', () => {
    expect(() => compileTableSeries('logs', [{ ...defaultTableSeries('logs'), aggFunc: 'avg', field: 'service' }], {})).toThrow();
    expect(() => compileTableSeries('metrics', [{ ...defaultTableSeries('metrics'), filters: [{ id: 'a', field: 'value', operator: 'gt', value: 'abc' }] }], {})).toThrow();
    expect(() => compileTableSeries('logs', [{ ...defaultTableSeries('logs'), filters: [{ id: 'a', field: 'unknown', operator: 'eq', value: 'x' }] }], {})).toThrow();
  });
  it('escapes literal LIKE wildcards while binding the complete pattern', () => {
    const params: SearchSqlParameters = {};
    const [item] = compileTableSeries('logs', [{ ...defaultTableSeries('logs'), filters: [{ id: 'a', field: 'body', operator: 'contains', value: '50%_' }] }], params);
    expect(item.where).toContain('ILIKE $cf0');
    expect(params.cf0).toBe('%50\\%\\_%');
  });
});
