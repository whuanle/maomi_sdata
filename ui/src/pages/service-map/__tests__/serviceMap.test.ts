import { describe, expect, it } from 'vitest';
import { buildMapQuery, buildMapExamples, type MapQuery } from '../serviceMapQuery';
import { decodeMap, layoutServices } from '../serviceMapModel';
import { parseMapState, serializeMapState } from '../serviceMapState';

export const query: MapQuery = { from: '2026-11-18T23:59:00Z', to: '2026-11-19T00:01:00Z', service: "api'gateway", syntax: 'sql', query: "status = 'error'", sampling: 100 };

describe('Service Map queries', () => {
  it('binds values, samples whole traces and joins both identity columns', () => {
    const request = buildMapQuery(query);
    expect(request.sql).not.toContain("api'gateway");
    expect(request.sql).toContain('AND (status = $cf0)');
    expect(request.parameters).toMatchObject({ service: "api'gateway", sampling: 100, cf0: 'error' });
    expect(request.sql).toContain('hash(trace_id)');
    expect(request.sql).toContain('c.trace_id = p.trace_id AND c.parent_span_id = p.span_id');
    expect(request.sql.indexOf('PARTITION BY w.trace_id, w.span_id')).toBeLessThan(request.sql.indexOf('LIMIT 100'));
    expect(request.sql).toContain('ts < $end');
  });
  it('compiles Lucene with the same bound semantics and rejects invalid sampling', () => {
    const request = buildMapQuery({ ...query, syntax: 'lucene', query: 'status:error' });
    expect(Object.values(request.parameters!)).toContain('error');
    expect(request.sql).not.toContain('status:error');
    expect(() => buildMapQuery({ ...query, sampling: 0 })).toThrow();
    expect(() => buildMapQuery({ ...query, query: '1=1; DELETE FROM traces' })).toThrow();
  });
  it('binds drilldown and preserves its original query window', () => {
    const request = buildMapExamples(query, { source: '<api>', target: "db'prod" });
    expect(request.parameters).toMatchObject({ source: '<api>', target: "db'prod" });
    expect(request.sql).not.toContain("db'prod");
    expect(request.from).toBe(query.from);
  });
});

describe('Service Map model', () => {
  it('decodes aggregates and flags separately hidden edges', () => {
    const result = decodeMap({ columns: ['type', 'source', 'target', 'calls', 'traces', 'errors', 'duration', 'first_seen', 'last_seen'], rows: [
      ['node', 'api', '', 10, 2, 1, 300, '2026-11-19', '2026-11-19'],
      ['meta', '', '', 1, 2, 3, 5, '4', '10'],
    ], elapsedMs: 1, truncated: false });
    expect(result).toMatchObject({ sampledTraces: 2, matchedTraces: 10, missingParents: 3, roots: 4, totalEdges: 5, truncated: true });
  });
  it('lays out cycles, disconnected components and downstream services deterministically', () => {
    const edges = [{ source: 'a', target: 'b' }, { source: 'b', target: 'a' }, { source: 'b', target: 'c' }];
    const layout = layoutServices(['c', 'b', 'a', 'isolated'], edges);
    expect(layout).toEqual(layoutServices(['isolated', 'a', 'b', 'c'], edges.slice().reverse()));
    expect(layout.find((node) => node.name === 'a')!.x).toBe(layout.find((node) => node.name === 'b')!.x);
    expect(layout.find((node) => node.name === 'c')!.x).toBeGreaterThan(layout.find((node) => node.name === 'b')!.x);
  });
  it('roundtrips URL state and safely defaults invalid values', () => {
    const state = { ...query, range: { from: query.from, to: query.to } };
    expect(parseMapState(serializeMapState(state))).toEqual({ range: state.range, service: state.service, query: state.query, syntax: state.syntax, sampling: state.sampling });
    expect(parseMapState(new URLSearchParams('sampling=-10&from=bad&to=bad')).sampling).toBe(100);
  });
});
