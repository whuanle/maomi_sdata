import type { AnalyticsQueryResponse } from '@/api/types';
import { EDGE_LIMIT, NODE_LIMIT } from './serviceMapQuery';

export interface MapMetric {
  source: string;
  target: string;
  calls: number;
  traces: number;
  errors: number;
  duration: number;
  firstSeen: string;
  lastSeen: string;
}

export interface MapResult {
  nodes: MapMetric[];
  edges: MapMetric[];
  buckets: { timestamp: number; traces: number }[];
  totalNodes: number;
  totalEdges: number;
  sampledTraces: number;
  matchedTraces: number;
  missingParents: number;
  roots: number;
  truncated: boolean;
}

export function decodeMap(response: AnalyticsQueryResponse): MapResult {
  const result: MapResult = { nodes: [], edges: [], buckets: [], totalNodes: 0, totalEdges: 0, sampledTraces: 0, matchedTraces: 0, missingParents: 0, roots: 0, truncated: response.truncated };
  const rows = response.rows.map((values) => Object.fromEntries(response.columns.map((key, index) => [key, values[index]])));
  for (const row of rows) {
    if (row.type === 'meta') {
      result.totalNodes = Number(row.calls);
      result.totalEdges = Number(row.duration);
      result.sampledTraces = Number(row.traces);
      result.missingParents = Number(row.errors);
      result.roots = Number(row.first_seen);
      result.matchedTraces = Number(row.last_seen);
    } else if (row.type === 'bucket') {
      result.buckets.push({ timestamp: Number(row.source), traces: Number(row.traces) });
    } else if (row.type === 'node' || row.type === 'edge') {
      const metric = { source: String(row.source ?? ''), target: String(row.target ?? ''), calls: Number(row.calls), traces: Number(row.traces), errors: Number(row.errors), duration: Number(row.duration), firstSeen: String(row.first_seen ?? ''), lastSeen: String(row.last_seen ?? '') };
      (row.type === 'node' ? result.nodes : result.edges).push(metric);
    }
  }
  result.buckets.sort((a, b) => a.timestamp - b.timestamp);
  result.truncated ||= result.totalNodes > NODE_LIMIT || result.totalEdges > result.edges.length || result.totalEdges > EDGE_LIMIT;
  return result;
}

/** 强连通分量内允许回环，分量之间按依赖层级排列；输入顺序不影响布局. */
export function layoutServices(nodes: string[], edges: { source: string; target: string }[]) {
  const names = [...new Set(nodes)].sort();
  const adjacency = new Map(names.map((name) => [name, [] as string[]]));
  for (const edge of edges) if (adjacency.has(edge.source) && adjacency.has(edge.target)) adjacency.get(edge.source)!.push(edge.target);
  for (const list of adjacency.values()) list.sort();
  const indices = new Map<string, number>();
  const low = new Map<string, number>();
  const stack: string[] = [];
  const active = new Set<string>();
  const groups: string[][] = [];
  let index = 0;
  function visit(name: string) {
    indices.set(name, index);
    low.set(name, index++);
    stack.push(name);
    active.add(name);
    for (const next of adjacency.get(name)!) {
      if (!indices.has(next)) { visit(next); low.set(name, Math.min(low.get(name)!, low.get(next)!)); }
      else if (active.has(next)) low.set(name, Math.min(low.get(name)!, indices.get(next)!));
    }
    if (low.get(name) === indices.get(name)) {
      const group: string[] = [];
      let item: string;
      do { item = stack.pop()!; active.delete(item); group.push(item); } while (item !== name);
      groups.push(group.sort());
    }
  }
  names.forEach((name) => { if (!indices.has(name)) visit(name); });
  const membership = new Map(groups.flatMap((group, id) => group.map((name) => [name, id] as const)));
  const parents = groups.map(() => new Set<number>());
  edges.forEach(({ source, target }) => {
    const from = membership.get(source), to = membership.get(target);
    if (from !== undefined && to !== undefined && from !== to) parents[to].add(from);
  });
  const levels = new Map<number, number>();
  function level(id: number): number {
    if (!levels.has(id)) levels.set(id, parents[id].size ? Math.max(...[...parents[id]].map(level)) + 1 : 0);
    return levels.get(id)!;
  }
  const lanes = new Map<number, string[]>();
  groups.forEach((group, id) => { const depth = level(id); lanes.set(depth, [...(lanes.get(depth) ?? []), ...group]); });
  return names.map((name) => {
    const depth = level(membership.get(name)!);
    const lane = lanes.get(depth)!.sort();
    return { name, x: depth * 240, y: (lane.indexOf(name) - (lane.length - 1) / 2) * 110 };
  });
}
