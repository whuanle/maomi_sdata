import { describe, expect, it } from 'vitest';
import { compileCustomNode, defaultNodeEditor } from '../customNodeBuilder';
import { newCustomNode } from '../customPanel';
const range = { from: '2026-11-19T00:00:00Z', to: '2026-11-19T01:00:00Z' };
describe('visual query compiler', () => {
  it('binds Lucene values and restores the original builder', () => {
    const node = newCustomNode(0);
    node.editor!.where = 'service:"api\'quoted"';
    const result = compileCustomNode(node, range);
    expect(Object.values(result.parameters)).toContain("api'quoted");
    expect(result.sql).not.toContain("api'quoted");
    expect(result.editor!.where).toBe(node.editor!.where);
    expect(result.shape).toBe('scalar');
  });
  it('maps time buckets and preserves automatic bucket binding', () => {
    const node = newCustomNode(0);
    node.editor!.shape = 'series'; node.editor!.groupBy = ['service'];
    const result = compileCustomNode(node, range);
    expect(result.timeField).toBe('ts_bucket');
    expect(result.groupFields).toEqual(['service']);
    expect(result.autoGranularity).toBe(true);
    expect(result.parameters.chart_bucket).toBe('1 minute');
    expect(result.sql).toContain('$chart_bucket');
  });
  it('binds metric names and label paths', () => {
    const node = newCustomNode(0);
    node.editor = { ...defaultNodeEditor(), source: 'metrics', aggregation: 'avg', field: 'value', metricName: "cpu'usage", metricFilters: [{ field: 'labels["host"]', value: "a'b" }] };
    const result = compileCustomNode(node, range);
    expect(Object.values(result.parameters)).toContain("cpu'usage");
    expect(Object.values(result.parameters)).toContain("a'b");
    expect(result.sql).not.toContain("cpu'usage");
  });
  it('compiles guided percentages and leaves legacy SQL untouched', () => {
    const node = newCustomNode(2, 'expression');
    node.editor!.operation = 'percentage';
    expect(compileCustomNode(node, range).expression).toBe('(A / B) * 100');
    node.editor = undefined;
    node.sql = 'SELECT $value AS value';
    expect(compileCustomNode(node, range)).toBe(node);
  });
  it('maps raw tables and rejects invalid Lucene', () => {
    const node = newCustomNode(0); node.editor!.shape = 'table';
    expect(compileCustomNode(node, range).shape).toBe('table');
    node.editor!.where = 'unknown_field:abc';
    expect(() => compileCustomNode(node, range)).toThrow();
  });
});
