import { describe, expect, it } from 'vitest';
import type { SpanItem } from '@/api/types';
import { buildServiceGraph } from '../ServiceMapTab';

/** 构造 span（缺省字段补齐）. */
function span(overrides: Partial<SpanItem> & Pick<SpanItem, 'spanId' | 'service'>): SpanItem {
  return {
    traceId: 't1',
    parentSpanId: null,
    timestamp: '2026-11-17T12:00:00Z',
    durationUs: 1000,
    name: 'op',
    kind: 'server',
    status: 'ok',
    ...overrides,
  };
}

describe('buildServiceGraph', () => {
  it('多级服务聚合：节点 span/错误/耗时汇总，边按 parent→child 聚合', () => {
    const graph = buildServiceGraph([
      span({ spanId: 's1', service: 'api-gateway', durationUs: 36_000, status: 'ok' }),
      span({ spanId: 's2', service: 'order-service', parentSpanId: 's1', durationUs: 30_000, status: 'ok' }),
      span({ spanId: 's3', service: 'inventory-service', parentSpanId: 's2', durationUs: 24_000, status: 'error' }),
      span({ spanId: 's4', service: 'inventory-service', parentSpanId: 's2', durationUs: 6_000, status: 'ok' }),
    ]);

    expect(graph.nodes).toEqual([
      { service: 'inventory-service', spans: 2, errors: 1, totalDurationUs: 30_000 },
      { service: 'api-gateway', spans: 1, errors: 0, totalDurationUs: 36_000 },
      { service: 'order-service', spans: 1, errors: 0, totalDurationUs: 30_000 },
    ]);
    expect(graph.edges).toEqual([
      { from: 'order-service', to: 'inventory-service', calls: 2, errors: 1, totalDurationUs: 30_000 },
      { from: 'api-gateway', to: 'order-service', calls: 1, errors: 0, totalDurationUs: 30_000 },
    ]);
  });

  it('同服务内部调用不成边、未知父 span 跳过、空 spanId 忽略', () => {
    const graph = buildServiceGraph([
      span({ spanId: 's1', service: 'order-service' }),
      span({ spanId: 's2', service: 'order-service', parentSpanId: 's1' }),
      span({ spanId: 's3', service: 'order-service', parentSpanId: 'missing' }),
      span({ spanId: '', service: 'ghost', parentSpanId: 's1' }),
    ]);

    expect(graph.nodes.map((node) => node.service)).toEqual(['order-service']);
    expect(graph.edges).toEqual([]);
  });

  it('单节点退化：无边可渲染', () => {
    const graph = buildServiceGraph([span({ spanId: 's1', service: 'solo', status: 'error' })]);
    expect(graph.nodes).toEqual([{ service: 'solo', spans: 1, errors: 1, totalDurationUs: 1000 }]);
    expect(graph.edges).toEqual([]);
  });

  it('空输入返回空图', () => {
    expect(buildServiceGraph([])).toEqual({ nodes: [], edges: [] });
  });
});
