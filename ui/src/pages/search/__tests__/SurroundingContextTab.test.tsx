import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse } from '@/api/types';
import { SurroundingContextTab } from '../SurroundingContextTab';

vi.mock('@/api/search', () => ({ searchSql: vi.fn() }));

const record = { ts: '2026-11-17T12:00:00.123456Z', service: 'api', body: 'selected' };
const observers: { callback: IntersectionObserverCallback; disconnect: ReturnType<typeof vi.fn> }[] = [];

function page(start: number, count: number, truncated = false): AnalyticsQueryResponse {
  return {
    columns: ['id', 'ts', 'service', 'severity', 'body'],
    rows: Array.from({ length: count }, (_, index) => [String(BigInt('9007199254740993') + BigInt(start + index)), record.ts, 'api', 'info', `log-${start + index}`]),
    elapsedMs: 1,
    truncated,
  };
}

function intersect() {
  act(() => observers[observers.length - 1]?.callback([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver));
}

beforeEach(() => {
  observers.length = 0;
  vi.mocked(searchSql).mockReset();
  vi.stubGlobal('IntersectionObserver', class {
    callback: IntersectionObserverCallback;
    disconnect = vi.fn();
    constructor(callback: IntersectionObserverCallback) {
      this.callback = callback;
      observers.push(this);
    }
    observe = vi.fn();
    unobserve = vi.fn();
  });
});

afterEach(() => vi.unstubAllGlobals());

describe('SurroundingContextTab', () => {
  it('traces 结果显示耗时，查看详情不移动锚点，显式重新定位才重查', async () => {
    const selected = { ...record, trace_id: 'trace', span_id: 'anchor' };
    const response: AnalyticsQueryResponse = { columns: ['ts', 'service', 'name', 'duration_us', 'status', 'trace_id', 'span_id'], rows: [['2026-11-17T12:00:01.123456Z', 'db', 'query', 500, 'ok', 'other', 'child']], elapsedMs: 1, truncated: false };
    vi.mocked(searchSql).mockResolvedValue(response);
    const onSelect = vi.fn();
    render(<SurroundingContextTab record={selected} currentTs={selected.ts} domain="traces" allowDomainSwitch onSelect={onSelect} />);
    await screen.findByText('500 µs');
    expect(vi.mocked(searchSql).mock.calls[0][0].sql).toContain('FROM {{traces}}');
    fireEvent.click(screen.getByText('query'));
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ span_id: 'child' }), 'traces');
    expect(searchSql).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: '以选中记录查看上下文' }));
    await waitFor(() => expect(searchSql).toHaveBeenCalledTimes(2));
    expect(vi.mocked(searchSql).mock.calls[1][0].from).toBe('2026-11-17T11:59:31.123456Z');
  });

  it('首批100条，重复触底只请求一次，追加后结束且不再请求', async () => {
    let resolveNext!: (value: AnalyticsQueryResponse) => void;
    vi.mocked(searchSql).mockResolvedValueOnce(page(0, 100, true))
      .mockImplementationOnce(() => new Promise((resolve) => { resolveNext = resolve; }));
    render(<SurroundingContextTab record={record} currentTs={record.ts} onSelect={vi.fn()} />);
    await screen.findByText('log-99');
    expect(screen.queryByText('已显示全部结果')).not.toBeInTheDocument();
    expect(vi.mocked(searchSql).mock.calls[0][0]).toMatchObject({ rowLimit: 100, parameters: { page_limit: 101, page_offset: 0 } });
    await waitFor(() => expect(observers.length).toBeGreaterThan(0));
    intersect();
    intersect();
    expect(searchSql).toHaveBeenCalledTimes(2);
    expect(vi.mocked(searchSql).mock.calls[1][0].parameters).toMatchObject({ page_offset: 100 });
    await act(async () => resolveNext(page(100, 100)));
    expect(screen.getByText('log-0')).toBeInTheDocument();
    expect(screen.getByText('log-199')).toBeInTheDocument();
    expect(screen.getByText('已显示全部结果')).toBeInTheDocument();
    expect(observers.every((observer) => observer.disconnect.mock.calls.length > 0)).toBe(true);
    intersect();
    expect(searchSql).toHaveBeenCalledTimes(2);
  });

  it.each([0, 99, 100])('仅有 %i 条时直接结束，不创建续页监听', async (count) => {
    vi.mocked(searchSql).mockResolvedValueOnce(page(0, count));
    render(<SurroundingContextTab record={record} currentTs={record.ts} onSelect={vi.fn()} />);
    await screen.findByText('已显示全部结果');
    expect(searchSql).toHaveBeenCalledTimes(1);
    expect(observers).toHaveLength(0);
  });

  it('续页失败保留已有行，重试相同offset', async () => {
    vi.mocked(searchSql).mockResolvedValueOnce(page(0, 100, true))
      .mockRejectedValueOnce(new Error('续页失败')).mockResolvedValueOnce(page(100, 1));
    render(<SurroundingContextTab record={record} currentTs={record.ts} onSelect={vi.fn()} />);
    await screen.findByText('log-99');
    await waitFor(() => expect(observers.length).toBeGreaterThan(0));
    intersect();
    await screen.findByText('续页失败');
    expect(screen.getByText('log-0')).toBeInTheDocument();
    expect(screen.queryByText('已显示全部结果')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('重试'));
    await screen.findByText('log-100');
    expect(vi.mocked(searchSql).mock.calls[2][0].parameters).toMatchObject({ page_offset: 100 });
    expect(screen.getByText('已显示全部结果')).toBeInTheDocument();
  });

  it('切换条件清空列表并忽略旧的续页响应', async () => {
    let resolveOld!: (value: AnalyticsQueryResponse) => void;
    vi.mocked(searchSql).mockResolvedValueOnce(page(0, 100, true))
      .mockImplementationOnce(() => new Promise((resolve) => { resolveOld = resolve; }))
      .mockResolvedValueOnce(page(300, 1));
    render(<SurroundingContextTab record={record} currentTs={record.ts} onSelect={vi.fn()} />);
    await screen.findByText('log-99');
    await waitFor(() => expect(observers.length).toBeGreaterThan(0));
    intersect();
    fireEvent.click(screen.getByRole('button', { name: '同服务' }));
    await screen.findByText('log-300');
    await act(async () => resolveOld(page(100, 100)));
    expect(screen.queryByText('log-0')).not.toBeInTheDocument();
    expect(screen.queryByText('log-199')).not.toBeInTheDocument();
    expect(vi.mocked(searchSql).mock.calls[2][0].parameters).toMatchObject({ page_offset: 0, service: 'api' });
  });
});
