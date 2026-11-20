import { useState } from 'react';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import '@/i18n';
import { searchSql } from '@/api/search';
import type { ExecuteSearchSqlRequest } from '@/api/types';
import { SearchSidePanel } from '../SearchSidePanel';
import type { SearchFilter } from '../filters';

vi.mock('@/api/search', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/search')>()),
  searchSql: vi.fn(),
}));

/** 属性组 key 枚举响应. */
const KEYS_RESPONSE = { columns: ['v', 'c'], rows: [['order.no', 19]], elapsedMs: 1, truncated: false };

/** 物理列值分布响应. */
const VALUES_RESPONSE = { columns: ['v', 'c'], rows: [['api-gateway', 100]], elapsedMs: 1, truncated: false };

/** 按 SQL 形状分发：key 枚举 / 其余值分布. */
function mockFacetResponses() {
  vi.mocked(searchSql).mockImplementation(async (request: ExecuteSearchSqlRequest) =>
    request.sql.includes('json_keys') ? KEYS_RESPONSE : VALUES_RESPONSE,
  );
}

/** 测试挂载壳：持有 filters 状态（模拟 SearchPage 的受控行为）. */
function Harness({ initialFilters = [], domain = 'logs' }: { initialFilters?: SearchFilter[]; domain?: 'logs' | 'traces' }) {
  const [filters, setFilters] = useState<SearchFilter[]>(initialFilters);
  return (
    <SearchSidePanel
      domain={domain}
      mode="table"
      onModeChange={vi.fn()}
      denoise={false}
      onDenoiseChange={vi.fn()}
      filters={filters}
      onFiltersChange={setFilters}
      from="2026-11-17T00:00:00Z"
      to="2026-11-17T01:00:00Z"
    />
  );
}

describe('SearchSidePanel 过滤器底栏', () => {
  beforeEach(() => {
    vi.mocked(searchSql).mockClear();
    mockFacetResponses();
  });

  it('traces 默认显示专属筛选，根节点和毫秒耗时可清除', async () => {
    render(<Harness domain="traces" />);
    expect(screen.getByTestId('search-filter-tree')).toBeTruthy();
    expect(screen.queryByTestId('search-denoise')).toBeNull();
    fireEvent.click(screen.getByRole('checkbox', { name: '仅根 Span' }));
    expect(screen.getByTestId('search-filter-chip').textContent).toContain('parent_span_id root');
    fireEvent.change(screen.getByRole('spinbutton', { name: '最小耗时' }), { target: { value: '0.25' } });
    expect(screen.getAllByTestId('search-filter-chip').some((chip) => chip.textContent?.includes('duration_us gte 250'))).toBe(true);
    fireEvent.change(screen.getByRole('spinbutton', { name: '最小耗时' }), { target: { value: '' } });
    expect(screen.getAllByTestId('search-filter-chip')).toHaveLength(1);
    await waitFor(() => expect(screen.getAllByRole('checkbox', { name: 'api-gateway' }).length).toBeGreaterThan(0));
  });

  it('过滤器树默认收起，手动内联表单（字段/操作符/值/新建）不再渲染', () => {
    render(<Harness />);
    expect(screen.queryByTestId('search-filter-tree')).toBeNull();
    expect(screen.getByTestId('search-toggle-filters').textContent).toContain('更多过滤器');
    expect(screen.queryByTestId('search-add-filter')).toBeNull();
    expect(screen.queryByText('新建')).toBeNull();
    expect(screen.getByText('暂无过滤器')).toBeTruthy();
  });

  it('点击「更多过滤器」展开属性过滤器树（facet 查询发起 + 提示文案），再点击收起', async () => {
    render(<Harness />);
    fireEvent.click(screen.getByTestId('search-toggle-filters'));
    expect(await screen.findByTestId('search-filter-tree')).toBeTruthy();
    await waitFor(() => {
      expect(vi.mocked(searchSql)).toHaveBeenCalled();
    });
    expect(screen.getByTestId('search-toggle-filters').textContent).toContain('收起过滤器');
    expect(screen.getByTestId('search-facet-hint').textContent).toContain('service:api');
    // 树展开态下手动表单同样不存在
    expect(screen.queryByText('新建')).toBeNull();

    fireEvent.click(screen.getByTestId('search-toggle-filters'));
    await waitFor(() => {
      expect(screen.queryByTestId('search-filter-tree')).toBeNull();
    });
    expect(screen.getByTestId('search-toggle-filters').textContent).toContain('更多过滤器');
  });

  it('过滤 chips 可手动移除（减少过滤器），清空后回到空态', () => {
    render(<Harness initialFilters={[{ id: 1, field: 'service', op: 'eq', value: 'api-gateway' }]} />);
    const chip = screen.getByTestId('search-filter-chip');
    expect(chip.textContent).toContain('service eq api-gateway');
    fireEvent.click(within(chip).getByRole('button', { name: 'remove filter' }));
    expect(screen.queryByTestId('search-filter-chip')).toBeNull();
    expect(screen.getByText('暂无过滤器')).toBeTruthy();
  });
});
