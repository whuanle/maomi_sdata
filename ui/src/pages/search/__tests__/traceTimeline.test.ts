import { describe, expect, it } from 'vitest';
import { buildTraceTree, spanEvents, timestampUs } from '../traceTimeline';
import { buildFilterConditionSql } from '../lucene';
import { parseSearchUrlState, serializeSearchUrlState } from '../searchUrlState';

describe('trace timeline', () => {
  it('保留微秒偏移，不截成毫秒', () => {
    expect(timestampUs('2026-11-17T10:00:00.123456Z') - timestampUs('2026-11-17T10:00:00.123001Z')).toBe(455);
  });

  it('父子树稳定排序且兼容全零根、孤儿和循环', () => {
    const spans = [
      { span_id: 'child', parent_span_id: 'root', ts: '2026-11-17T10:00:00.001Z' },
      { span_id: 'root', parent_span_id: '0000000000000000', ts: '2026-11-17T10:00:00Z' },
      { span_id: 'orphan', parent_span_id: 'missing', ts: '2026-11-17T10:00:00.002Z' },
      { span_id: 'cycle-a', parent_span_id: 'cycle-b', ts: '2026-11-17T10:00:00.003Z' },
      { span_id: 'cycle-b', parent_span_id: 'cycle-a', ts: '2026-11-17T10:00:00.004Z' },
    ];
    const tree = buildTraceTree(spans);
    expect(tree.rows.map((row) => row.span.span_id)).toEqual(['root', 'child', 'orphan', 'cycle-a', 'cycle-b']);
    expect(tree.rows[1].depth).toBe(1);
    expect(tree.incomplete).toBe(true);
    expect(buildTraceTree(spans.slice(0, 2)).incomplete).toBe(false);
  });

  it('兼容 OTLP 历史事件纳秒时间和规范化事件', () => {
    const events = spanEvents(JSON.stringify([{ name: 'cache', timeUnixNano: '1794909600123456000', attributes: { hit: true } }]));
    expect(events[0].timestamp).toBe('2026-11-17T10:00:00.123456Z');
    expect(events[0].attributes).toEqual({ hit: true });
    expect(spanEvents('bad json')).toEqual([]);
  });

  it('根过滤限定 traces 父 ID，耗时参数绑定，URL 可恢复', () => {
    const params = {};
    expect(buildFilterConditionSql('parent_span_id', 'root', '', 'traces', params)).toContain("parent_span_id = '0000000000000000'");
    expect(() => buildFilterConditionSql('trace_id', 'root', '', 'traces', {})).toThrow();
    expect(buildFilterConditionSql('duration_us', 'gte', '250', 'traces', params)).toContain('$p0');
    expect(params).toEqual({ p0: 250 });
    const state = parseSearchUrlState(new URLSearchParams('source=traces'));
    state.filters = [{ id: 1, field: 'parent_span_id', op: 'root', value: '' }];
    const encoded = serializeSearchUrlState(state);
    expect(parseSearchUrlState(new URLSearchParams(encoded)).filters[0].op).toBe('root');
  });
});
