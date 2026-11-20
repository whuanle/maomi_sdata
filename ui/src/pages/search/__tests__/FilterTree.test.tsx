import { useState } from 'react';
import { render, screen, waitFor, within, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import '@/i18n';
import type { ExecuteSearchSqlRequest } from '@/api/types';
import { FilterTree } from '../FilterTree';
import type { SearchFilter } from '../filters';
import { searchSql } from '@/api/search';

vi.mock('@/api/search', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/search')>()),
  searchSql: vi.fn(),
}));

/** 属性组 key 枚举响应（含点分 key）. */
const KEYS_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['order.no', 19], ['sku', 19], ['user.id', 19]],
  elapsedMs: 1,
  truncated: false,
};

/** order.no 的值分布响应. */
const ORDER_VALUES_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['A-100', 12], ['B-200', 7]],
  elapsedMs: 1,
  truncated: false,
};

/** order.no 节点的子属性枚举响应（发现树懒加载）. */
const CHILD_KEYS_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['status', 7]],
  elapsedMs: 1,
  truncated: false,
};

/** order.no.status 的值分布响应. */
const NESTED_VALUES_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['ok', 5]],
  elapsedMs: 1,
  truncated: false,
};

/** 物理列值分布响应（service/severity/source/resource 值查询共用）. */
const PHYSICAL_VALUES_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['api-gateway', 100], ['order-service', 50]],
  elapsedMs: 1,
  truncated: false,
};

/** 服务端搜索（ILIKE）命中后的 key 枚举响应. */
const FILTERED_KEYS_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['pay.channel', 76]],
  elapsedMs: 1,
  truncated: false,
};

/** 服务端搜索（ILIKE）命中后的值分布响应. */
const FILTERED_VALUES_RESPONSE = {
  columns: ['v', 'c'],
  rows: [['A-100', 12]],
  elapsedMs: 1,
  truncated: false,
};

/** 生成 n 行值分布响应（供截断/Load more 用例）. */
function valuesResponseOfSize(n: number) {
  return {
    columns: ['v', 'c'],
    rows: Array.from({ length: n }, (_, i) => [`value-${i}`, i + 1]),
    elapsedMs: 1,
    truncated: false,
  };
}

/** 按 SQL 形状分发：key 枚举 / 子属性枚举 / order.no 值分布（绑定路径参数）/ 嵌套路径值分布 / service 列 / 其余物理列；含 ILIKE 时返回过滤态响应. */
function mockFacetResponses() {
  vi.mocked(searchSql).mockImplementation(async (request: ExecuteSearchSqlRequest) => {
    const searched = request.sql.includes('ILIKE');
    const pathValues = Object.values(request.parameters ?? {}).filter((value) => typeof value === 'string' && value.startsWith('$'));
    if (request.sql.includes('json_keys') && request.sql.includes('json_extract(')) return CHILD_KEYS_RESPONSE;
    if (pathValues.includes('$."order.no"."status"')) return searched ? FILTERED_VALUES_RESPONSE : NESTED_VALUES_RESPONSE;
    if (pathValues.includes('$."order.no"')) return searched ? FILTERED_VALUES_RESPONSE : ORDER_VALUES_RESPONSE;
    if (request.sql.includes('json_keys')) return searched ? FILTERED_KEYS_RESPONSE : KEYS_RESPONSE;
    return searched ? FILTERED_VALUES_RESPONSE : PHYSICAL_VALUES_RESPONSE;
  });
}

/** 测试挂载壳：持有 filters 状态并记录每次变更（模拟 SearchPage 的受控行为；capturedRef 供断言）. */
const capturedRef: { changes: SearchFilter[][] } = { changes: [] };
function Harness({ initialFilters = [], denoise = false, captured }: {
  initialFilters?: SearchFilter[];
  denoise?: boolean;
  captured?: { changes: SearchFilter[][] };
}) {
  const [filters, setFilters] = useState<SearchFilter[]>(initialFilters);
  return (
    <FilterTree
      domain="logs"
      from="2026-11-17T00:00:00Z"
      to="2026-11-17T01:00:00Z"
      denoise={denoise}
      filters={filters}
      onFiltersChange={(next) => {
        capturedRef.changes.push(next);
        captured?.changes.push(next);
        setFilters(next);
      }}
    />
  );
}

describe('FilterTree', () => {
  beforeEach(() => {
    capturedRef.changes.length = 0;
    vi.mocked(searchSql).mockClear();
    mockFacetResponses();
  });

  it('渲染全部分组：JSON 组 {n} distinct key 数、物理组 (n) distinct 值数', async () => {
    render(<Harness />);
    await waitFor(() => {
      expect(vi.mocked(searchSql)).toHaveBeenCalled();
    });
    expect(screen.getByTestId('search-filter-group-resource').textContent).toContain('资源属性');
    expect(screen.getByTestId('search-filter-group-attributes').textContent).toContain('日志属性');
    expect(screen.getByTestId('search-filter-group-service').textContent).toContain('服务名');
    expect(screen.getByTestId('search-filter-group-severity').textContent).toContain('日志级别');
    expect(screen.getByTestId('search-filter-group-source').textContent).toContain('来源');
    // 日志属性默认展开：{3} = distinct key 数；服务名 (2) = distinct 值数
    await waitFor(() => {
      expect(screen.getByTestId('search-filter-group-attributes').textContent).toContain('{3}');
    });
    expect(screen.getByTestId('search-filter-group-service').textContent).toContain('(2)');
    // 默认展开组内 key 行可见（点分 key 直显）
    expect(screen.getByTestId('search-filter-key-attributes.order.no').textContent).toContain('order.no');
    expect(screen.getByTestId('search-filter-key-attributes.order.no').textContent).toContain('(19)');
  });

  it('facet 查询走直查通道：key 枚举含 json_keys、时间参数与行抽样，denoise 附加级别排除', async () => {
    render(<Harness denoise />);
    await waitFor(() => {
      expect(vi.mocked(searchSql)).toHaveBeenCalled();
    });
    const keysCall = vi.mocked(searchSql).mock.calls.map(([request]) => request).find((request) => request.sql.includes('json_valid(attributes)'));
    expect(keysCall).toBeTruthy();
    expect(keysCall!.sql).toContain('FROM {{logs}}');
    expect(keysCall!.sql).toContain('ts >= $start AND ts < $end');
    expect(keysCall!.sql).toContain("severity NOT IN ('DEBUG', 'TRACE')");
    expect(keysCall!.sql).toContain('json_valid(attributes)');
    // 计数为行抽样估计（ClickStack 式）：固定 1000 行 reservoir，非全量 GROUP BY 扫描
    expect(keysCall!.sql).toContain('USING SAMPLE 1000 ROWS');
    expect(keysCall!.from).toBe('2026-11-17T00:00:00Z');
  });

  it('展开 key 懒加载值列表：JSON 字面引号路径 + 值行三态复选框', async () => {
    render(<Harness />);
    expect(await screen.findByTestId('search-filter-key-attributes.order.no')).toBeTruthy();
    fireEvent.click(screen.getByTestId('search-filter-key-attributes.order.no'));
    expect(await screen.findByText('A-100')).toBeTruthy();
    expect(screen.getByText('B-200')).toBeTruthy();
    const valuesCall = vi.mocked(searchSql).mock.calls
      .map(([request]) => request)
      .find((request) => request.sql.includes('json_valid(attributes') && !request.sql.includes('json_keys'));
    expect(valuesCall!.sql).toContain('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END,');
    expect(valuesCall!.sql).toContain('WHERE v IS NOT NULL');
    // 提取路径经服务端命名参数绑定（$jpN），不在 SQL 文本中出现字面路径
    expect(valuesCall!.sql).not.toContain('order.no');
    expect(Object.values(valuesCall!.parameters ?? {})).toContain('$."order.no"');
  });

  it('勾选值并入 in 过滤，再勾选合并列表，取消勾选收缩', async () => {
    const captured = { changes: [] as SearchFilter[][] };
    render(<Harness captured={captured} />);
    fireEvent.click(await screen.findByTestId('search-filter-key-attributes.order.no'));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'A-100' }));
    await waitFor(() => {
      const last = captured.changes[captured.changes.length - 1];
      expect(last?.some((filter) => filter.op === 'in' && filter.values?.includes('A-100'))).toBe(true);
    });

    // 勾选 B-200：合并进同一 in 过滤（组内 OR）
    fireEvent.click(screen.getByRole('checkbox', { name: 'B-200' }));
    await waitFor(() => {
      const last = captured.changes[captured.changes.length - 1];
      expect(last?.some((filter) => filter.op === 'in' && filter.values?.length === 2)).toBe(true);
    });

    // 取消 A-100：in 过滤收缩为单值
    fireEvent.click(screen.getByRole('checkbox', { name: 'A-100' }));
    await waitFor(() => {
      const last = captured.changes[captured.changes.length - 1];
      const inFilter = last?.find((filter) => filter.op === 'in');
      expect(inFilter?.values).toEqual(['B-200']);
    });
  });

  it('值行「排除」文字操作并入 nin 过滤（半选 + 删除线），再次点击取消', async () => {
    const captured = { changes: [] as SearchFilter[][] };
    render(<Harness captured={captured} />);
    fireEvent.click(await screen.findByTestId('search-filter-key-attributes.order.no'));
    const row = await screen.findByTestId('search-filter-value-B-200');
    fireEvent.click(within(row).getByTestId('search-filter-value-exclude-B-200'));
    await waitFor(() => {
      const last = captured.changes[captured.changes.length - 1];
      expect(last?.some((filter) => filter.op === 'nin' && filter.values?.includes('B-200'))).toBe(true);
    });
    // 排除态：复选框半选 + 值文本删除线
    const checkbox = screen.getByRole('checkbox', { name: 'B-200' }) as HTMLInputElement;
    expect(checkbox.indeterminate).toBe(true);
    expect(row.querySelector('.line-through')).toBeTruthy();

    // 再次点击取消排除
    fireEvent.click(within(row).getByTestId('search-filter-value-exclude-B-200'));
    await waitFor(() => {
      expect(captured.changes[captured.changes.length - 1]?.some((filter) => filter.op === 'nin')).toBe(false);
    });
  });

  it('值行「仅此值」清空该字段选择仅保留此值（HyperDX Only 语义）', async () => {
    const captured = { changes: [] as SearchFilter[][] };
    render(<Harness captured={captured} />);
    fireEvent.click(await screen.findByTestId('search-filter-key-attributes.order.no'));
    // 先勾选 A-100 与 B-200（in 列表两值）
    fireEvent.click(await screen.findByRole('checkbox', { name: 'A-100' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'B-200' }));
    await waitFor(() => {
      expect(captured.changes[captured.changes.length - 1]?.some((filter) => filter.op === 'in' && filter.values?.length === 2)).toBe(true);
    });

    // 对 A-100 点「仅此值」：in 过滤收缩为单值 A-100
    fireEvent.click(screen.getByTestId('search-filter-value-only-A-100'));
    await waitFor(() => {
      const inFilter = captured.changes[captured.changes.length - 1]?.find((filter) => filter.op === 'in');
      expect(inFilter?.values).toEqual(['A-100']);
    });
  });

  it('key 行 +/⊘ 生成 exists/nexists 过滤（互斥，再次点击取消）', async () => {
    const captured = { changes: [] as SearchFilter[][] };
    render(<Harness captured={captured} />);
    const keyRow = await screen.findByTestId('search-filter-key-attributes.order.no');
    const rowContainer = keyRow.parentElement!;
    fireEvent.click(within(rowContainer).getByRole('button', { name: '加入过滤（键存在）' }));
    await waitFor(() => {
      expect(captured.changes[captured.changes.length - 1]?.some((filter) => filter.field === 'attributes.order.no' && filter.op === 'exists')).toBe(true);
    });

    // 直接切换为排除：exists 被 nexists 替换（互斥）
    fireEvent.click(within(rowContainer).getByRole('button', { name: '排除（键不存在）' }));
    await waitFor(() => {
      const last = captured.changes[captured.changes.length - 1]!;
      expect(last.some((filter) => filter.field === 'attributes.order.no' && filter.op === 'nexists')).toBe(true);
      expect(last.some((filter) => filter.field === 'attributes.order.no' && filter.op === 'exists')).toBe(false);
    });

    // 再次点击取消
    fireEvent.click(within(rowContainer).getByRole('button', { name: '排除（键不存在）' }));
    await waitFor(() => {
      expect(captured.changes[captured.changes.length - 1]?.some((filter) => filter.field === 'attributes.order.no')).toBe(false);
    });
  });

  it('值列表截断时显示 Load more，点击提高 LIMIT 重查', async () => {
    vi.mocked(searchSql).mockImplementation(async (request: ExecuteSearchSqlRequest) =>
      request.sql.includes('SELECT service AS v') ? valuesResponseOfSize(10) : PHYSICAL_VALUES_RESPONSE,
    );
    render(<Harness />);
    fireEvent.click(await screen.findByTestId('search-filter-group-service'));
    expect(await screen.findByText('value-0')).toBeTruthy();
    // 物理列组仅组头一个搜索框（ValueList 不再渲染第二个，避免双框）
    expect(screen.getByTestId('search-filter-keys-search-service')).toBeTruthy();
    expect(screen.queryByTestId('search-filter-values-search-service')).toBeNull();
    fireEvent.click(screen.getByTestId('search-filter-load-more'));
    await waitFor(() => {
      const call = vi.mocked(searchSql).mock.calls
        .map(([request]) => request)
        .find((request) => request.sql.includes('SELECT service AS v') && request.sql.includes('LIMIT 60'));
      expect(call).toBeTruthy();
    });
    // 截断态解除（10 < 60）：Load more 按钮消失
    await waitFor(() => {
      expect(screen.queryByTestId('search-filter-load-more')).toBeNull();
    });
  });

  it('值搜索走服务端 ILIKE：$key 参数绑定，无需枚举即可搜出条目', async () => {
    render(<Harness />);
    fireEvent.click(await screen.findByTestId('search-filter-key-attributes.order.no'));
    fireEvent.change(await screen.findByTestId('search-filter-values-search-attributes.order.no'), { target: { value: 'A_' } });
    // 防抖后重查：ILIKE 条件 + 转义后的 $key 参数（用户输入不参与 SQL 组装）；搜索态无行抽样（全窗扫描，稀有值可搜）
    await waitFor(() => {
      const call = vi.mocked(searchSql).mock.calls
        .map(([request]) => request)
        .find((request) => request.sql.includes('json_valid(attributes') && !request.sql.includes('json_keys') && request.sql.includes('ILIKE $key'));
      expect(call).toBeTruthy();
      expect(call!.parameters?.key).toBe('%A\\_%');
      expect(call!.sql).not.toContain('USING SAMPLE');
    });
    // 服务端过滤结果：仅命中值可见
    expect(await screen.findByText('A-100')).toBeTruthy();
    expect(screen.queryByText('B-200')).toBeNull();
  });

  it('属性组搜索框服务端过滤 key 枚举（$key ILIKE）', async () => {
    render(<Harness />);
    fireEvent.change(await screen.findByTestId('search-filter-keys-search-attributes'), { target: { value: 'pay.channel' } });
    await waitFor(() => {
      const call = vi.mocked(searchSql).mock.calls
        .map(([request]) => request)
        .find((request) => request.sql.includes('json_keys') && request.sql.includes('ILIKE $key'));
      expect(call).toBeTruthy();
      expect(call!.parameters?.key).toBe('%pay.channel%');
      expect(call!.sql).not.toContain('USING SAMPLE');
    });
    expect(await screen.findByTestId('search-filter-key-attributes.pay.channel')).toBeTruthy();
    expect(screen.queryByTestId('search-filter-key-attributes.order.no')).toBeNull();
  });

  it('key 行子属性浏览：{} 懒加载 json_keys 逐层枚举，嵌套 key 可展开值分布与存在性过滤', async () => {
    render(<Harness />);
    // 展开 order.no 的子属性（json_extract 取节点 + json_keys 枚举，路径参数绑定）
    fireEvent.click(await screen.findByTestId('search-filter-key-children-attributes.order.no'));
    await waitFor(() => {
      const call = vi.mocked(searchSql).mock.calls
        .map(([request]) => request)
        .find((request) => request.sql.includes('json_keys') && request.sql.includes('json_extract('));
      expect(call).toBeTruthy();
      expect(call!.sql).not.toContain('$."order.no"');
      expect(Object.values(call!.parameters ?? {})).toContain('$."order.no"');
    });
    // 嵌套 key 行（括号路径形态）出现并可继续展开值分布
    fireEvent.click(await screen.findByTestId('search-filter-key-attributes["order.no"]["status"]'));
    expect(await screen.findByText('ok')).toBeTruthy();
    const valuesCall = vi.mocked(searchSql).mock.calls
      .map(([request]) => request)
      .find((request) => Object.values(request.parameters ?? {}).includes('$."order.no"."status"'));
    expect(valuesCall).toBeTruthy();
    expect(valuesCall!.sql).toContain('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END,');
    // 嵌套 key 行的存在性过滤（⊕）生成绑定路径的 exists 条件
    fireEvent.click(within(screen.getByTestId('search-filter-key-attributes["order.no"]["status"]').parentElement!).getByRole('button', { name: '加入过滤（键存在）' }));
    await waitFor(() => {
      expect(capturedRef.changes.some((filters) => filters.some((filter) => filter.field === 'attributes["order.no"]["status"]' && filter.op === 'exists'))).toBe(true);
    });
  });
});
