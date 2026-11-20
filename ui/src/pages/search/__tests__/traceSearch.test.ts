import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadSpan, loadTraceLogs, loadTraceWindow } from '@/api/traceSearch';
import { searchSql } from '@/api/search';

vi.mock('@/api/search', () => ({ searchSql: vi.fn() }));

beforeEach(() => {
  vi.mocked(searchSql).mockReset();
  vi.mocked(searchSql).mockResolvedValue({
    columns: ['trace_id', 'span_id', 'attributes'],
    rows: [['trace', 'span', '{"cache.hit":true}']],
    elapsedMs: 1, truncated: false,
  });
});

describe('traceSearch', () => {
  it('按 trace/span 身份参数绑定补查完整记录', async () => {
    const row = await loadSpan({ trace_id: "trace' OR 1=1", span_id: 'span', ts: '2026-11-17T10:00:00.123456Z' });
    expect(row?.attributes).toBe('{"cache.hit":true}');
    const request = vi.mocked(searchSql).mock.calls[0][0];
    expect(request.sql).toContain('trace_id = $trace_id AND span_id = $span_id');
    expect(request.sql).not.toContain("trace' OR 1=1");
    expect(request.parameters).toEqual({ trace_id: "trace' OR 1=1", span_id: 'span' });
    expect(request.from).toBe('2026-11-17T00:00:00.000Z');
    expect(request.to).toBe('2026-11-18T00:00:00.000Z');
  });

  it('缺身份不进行可能匹配错误记录的补查', async () => {
    expect(await loadSpan({ ts: 'invalid' })).toBeNull();
    expect(searchSql).not.toHaveBeenCalled();
  });

  it('链路和日志沿用历史窗口且多读取一行检测截断', async () => {
    const from = '2025-01-01T00:00:00Z';
    const to = '2025-01-02T00:00:00Z';
    await loadTraceWindow('trace', from, to);
    await loadTraceLogs('trace', from, to);
    const requests = vi.mocked(searchSql).mock.calls.map(([request]) => request);
    expect(requests[0]).toMatchObject({ from, to, rowLimit: 10000, parameters: { trace_id: 'trace' } });
    expect(requests[0].sql).toContain('LIMIT 10001');
    expect(requests[1]).toMatchObject({ from, to, rowLimit: 200 });
    expect(requests[1].sql).toContain('LIMIT 201');
  });
});
