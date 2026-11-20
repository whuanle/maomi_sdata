import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { ResultsTable } from '../ResultsTable';

const props = {
  columns: ['body'],
  rows: [{ body: 'a log' }],
  onSort: vi.fn(), onAddFilter: vi.fn(), onTraceOpen: vi.fn(),
  onRowOpen: vi.fn(), onFilterByValue: vi.fn(),
};

afterEach(() => vi.unstubAllGlobals());

describe('ResultsTable footer', () => {
  it('只有完整结果显示结束标识，有续页不显示上限文案', () => {
    const { rerender } = render(<ResultsTable {...props} complete />);
    expect(screen.getByText('已显示全部结果')).toBeInTheDocument();
    rerender(<ResultsTable {...props} hasMore />);
    expect(screen.queryByText('已显示全部结果')).not.toBeInTheDocument();
    expect(screen.queryByText(/已达显示上限/)).not.toBeInTheDocument();
  });

  it('未查询、加载或失败时不显示结束标识', () => {
    const { rerender } = render(<ResultsTable {...props} />);
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    rerender(<ResultsTable {...props} loading complete />);
    expect(screen.queryByText('已显示全部结果')).not.toBeInTheDocument();
    rerender(<ResultsTable {...props} loadingMore complete />);
    expect(screen.queryByText('已显示全部结果')).not.toBeInTheDocument();
    rerender(<ResultsTable {...props} loadMoreError="failed" complete />);
    expect(screen.queryByText('已显示全部结果')).not.toBeInTheDocument();
    rerender(<ResultsTable {...props} rows={[]} empty={<div>empty</div>} />);
    expect(screen.getByText('empty')).toBeInTheDocument();
  });

  it('观察内部滚动容器，追加保留展开和滚动，新查询复位', () => {
    let callback!: IntersectionObserverCallback;
    let options!: IntersectionObserverInit;
    const disconnect = vi.fn();
    vi.stubGlobal('IntersectionObserver', class {
      constructor(cb: IntersectionObserverCallback, init: IntersectionObserverInit) { callback = cb; options = init; }
      observe = vi.fn();
      disconnect = disconnect;
    });
    const onLoadMore = vi.fn();
    const { rerender } = render(<ResultsTable {...props} hasMore onLoadMore={onLoadMore} queryGeneration={1} />);
    const scroll = screen.getByTestId('search-results-scroll');
    expect(options.root).toBe(scroll);
    act(() => callback([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver));
    expect(onLoadMore).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: '行明细' }));
    scroll.scrollTop = 120;
    rerender(<ResultsTable {...props} rows={[...props.rows, { body: 'second' }]} hasMore onLoadMore={onLoadMore} queryGeneration={1} />);
    expect(scroll.scrollTop).toBe(120);
    expect(screen.getByRole('button', { name: '按此值过滤' })).toBeInTheDocument();
    rerender(<ResultsTable {...props} complete queryGeneration={2} />);
    expect(scroll.scrollTop).toBe(0);
    expect(screen.queryByRole('button', { name: '按此值过滤' })).not.toBeInTheDocument();
    expect(disconnect).toHaveBeenCalled();
  });
});
