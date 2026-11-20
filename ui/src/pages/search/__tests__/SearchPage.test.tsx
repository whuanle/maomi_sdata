import { act, render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MemoryRouter, useLocation, useNavigate } from 'react-router';
import dayjs from 'dayjs';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import i18n from '@/i18n';
// jsdom 无 canvas，mock echarts 封装组件
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="mock-chart" /> }));
// jsdom 下 CodeMirror 测量受限，mock SQL 编辑器
vi.mock('../SqlEditor', () => ({
  SqlEditor: ({ value }: { value: string }) => <div data-testid="mock-sql-editor">{value}</div>,
}));
import { SearchPage } from '../SearchPage';
import { searchSql } from '@/api/search';
import { getSavedQuery, saveQuery } from '@/api/analytics';
import { useAuthStore } from '@/store/auth';

// mock SQL 直查（工厂会被提升，内部用字面量；具体实现按 SQL 形状在 beforeEach 分发）
vi.mock('@/api/search', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/search')>()),
  searchSql: vi.fn(),
}));

vi.mock('@/api/analytics', () => ({
  listSavedQueries: vi.fn().mockResolvedValue({ page: 1, pageSize: 50, count: 1, data: [
    { id: 2, name: '慢链路', kind: 'search', sqlText: '', definition: '{"domain":"traces","sql":"SELECT ts FROM {{traces}} WHERE ts >= $start","rangeMs":900000}', description: '', createTime: '2026-11-16T10:00:00Z', updateTime: '2026-11-16T10:00:00Z' },
  ] }),
  getSavedQuery: vi.fn().mockResolvedValue({ id: 2, name: '慢链路', kind: 'search', sqlText: '', definition: '{"domain":"logs","mode":"lucene","where":"severity:ERROR timeout","fields":"ts, service","orderBy":"ts DESC","limit":200,"rangeMs":900000}', description: '', createTime: '2026-11-16T10:00:00Z', updateTime: '2026-11-16T10:00:00Z' }),
  saveQuery: vi.fn().mockResolvedValue({ value: 2 }),
  deleteSavedQuery: vi.fn().mockResolvedValue({}),
}));

/** 断言最近一次行查询请求（非直方图聚合）的 SQL 片段与命名参数. */
function expectLastRequest(sqlContains: string[], parameters?: Record<string, unknown>) {
  const calls = vi.mocked(searchSql).mock.calls.filter(([request]) => !request.sql.includes('GROUP BY'));
  const request = calls[calls.length - 1][0];
  for (const fragment of sqlContains) {
    expect(request.sql).toContain(fragment);
  }

  if (parameters !== undefined) {
    expect(request.parameters).toEqual({ ...parameters, page_limit: request.rowLimit! + 1, page_offset: 0 });
  }

  return request;
}

/** 地址探针：上报每次路由变化后的 query（渲染期收集到外部数组，供 URL 同步断言）. */
function LocationProbe({ onChange }: { onChange: (search: string) => void }) {
  const { search } = useLocation();
  onChange(search);
  return null;
}

/** 返回探针：模拟浏览器回退（历史栈 -1）. */
function BackButton() {
  const navigate = useNavigate();
  return <button type="button" onClick={() => navigate(-1)} data-testid="test-back">back</button>;
}

const observers: { callback: IntersectionObserverCallback; disconnect: ReturnType<typeof vi.fn> }[] = [];
function intersect() {
  act(() => observers[observers.length - 1]?.callback([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver));
}
afterEach(() => vi.unstubAllGlobals());

describe('SearchPage', () => {
  beforeEach(() => {
    observers.length = 0;
    vi.stubGlobal('IntersectionObserver', class {
      callback: IntersectionObserverCallback;
      disconnect = vi.fn();
      constructor(callback: IntersectionObserverCallback) { this.callback = callback; observers.push(this); }
      observe = vi.fn();
      unobserve = vi.fn();
    });
    useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
    vi.mocked(searchSql).mockClear();
    // 按 SQL 形状分发：行查询返回 1 行日志；直方图聚合（GROUP BY）返回全量分桶计数。
    // 每个用例前重设，避免个别用例的 mockResolvedValue 覆盖后续用例
    vi.mocked(searchSql).mockImplementation(async (request) =>
      request.sql.includes('GROUP BY')
        ? {
            columns: ['bucket', 'grp', 'cnt'],
            rows: [
              [Date.now() - 60_000, 'ERROR', 2000],
              [Date.now() - 30_000, 'INFO', 1838],
            ],
            elapsedMs: 3,
            truncated: false,
          }
        : {
            columns: ['ts', 'service', 'severity', 'body', 'trace_id'],
            rows: [['2026-11-16T10:00:01Z', 'api-server', 'ERROR', '数据库连接超时', 'abc123def4567890']],
            elapsedMs: 12,
            truncated: false,
          },
    );
  });

  it('Lucene 模式默认渲染：编译查询自动运行并展示结果', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    // 默认查询经编译器生成 SQL 直查（{{logs}} 占位符 + $start/$end + 默认排序与行数）
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    expectLastRequest(['FROM {{logs}}', '$start', 'ORDER BY ts DESC', 'LIMIT CAST($page_limit AS BIGINT)']);
    // HyperDX 式工具栏：WHERE 搜索栏 / 显示字段 / 排序 / 实时追踪 / 运行 / 保存
    expect(screen.getByTestId('search-where')).toBeTruthy();
    expect(screen.getByTestId('search-fields')).toBeTruthy();
    expect(screen.getByTestId('search-order-by')).toBeTruthy();
    expect(screen.getByTestId('search-live-tail')).toBeTruthy();
    expect(screen.getByTestId('search-run')).toBeTruthy();
    expect(screen.getByText('保存搜索')).toBeTruthy();
    expect(screen.getByTestId('search-saved-button')).toBeTruthy();
    // 左侧分析面板：分析模式 + 过滤器
    expect(screen.getByTestId('search-side-panel')).toBeTruthy();
    expect(screen.getByText('分析模式')).toBeTruthy();
    expect(screen.getByText('结果表')).toBeTruthy();
    expect(screen.getByText('事件模式')).toBeTruthy();
    expect(screen.getByText('过滤器')).toBeTruthy();
    // 结果行（动态列）+ 直方图 + 结果头部计数（聚合总数 2000+1838）
    await waitFor(() => {
      expect(screen.getByText('数据库连接超时')).toBeTruthy();
    });
    expect(screen.getByTestId('mock-chart')).toBeTruthy();
    expect(screen.getByTestId('search-result-count').textContent).toContain('3,838');
    // 保存的搜索抽屉打开后可见列表项
    fireEvent.click(screen.getByTestId('search-saved-button'));
    expect(await screen.findByText('慢链路')).toBeTruthy();
  });

  it.each(['zh-CN', 'en-US'])('有续页时不显示截断或上限提示（%s）', async (language) => {
    await i18n.changeLanguage(language);
    const defaultSearch = vi.mocked(searchSql).getMockImplementation()!;
    vi.mocked(searchSql).mockImplementation(async (request) => {
      const response = await defaultSearch(request);
      return request.sql.includes('GROUP BY')
        ? response
        : { ...response, rows: [...response.rows, ...response.rows], truncated: true };
    });
    const { unmount } = render(
      <MemoryRouter initialEntries={['/search?source=logs&limit=200']}>
        <SearchPage />
      </MemoryRouter>,
    );
    try {
      await waitFor(() => expect(screen.getAllByText('数据库连接超时').length).toBeGreaterThan(0));
      expect(screen.queryByText(/Results truncated|结果已按行数上限截断|Display limit reached|已达显示上限/)).toBeNull();
      expect(screen.queryByText(/End of Results|已显示全部结果/)).toBeNull();
      expectLastRequest(['LIMIT CAST($page_limit AS BIGINT)']);
      expect(screen.getByTestId('search-result-count').textContent).toContain('3,838');
    } finally {
      unmount();
      await i18n.changeLanguage('zh-CN');
    }
  });

  it.each(['lucene', 'sql'])('触底追加分页，固定已执行条件且不重查直方图（%s）', async (mode) => {
    const defaultSearch = vi.mocked(searchSql).getMockImplementation()!;
    let resolveNext!: (value: Awaited<ReturnType<typeof searchSql>>) => void;
    vi.mocked(searchSql).mockImplementation(async (request) => {
      const response = await defaultSearch(request);
      if (request.sql.includes('GROUP BY')) return response;
      if (request.parameters?.page_offset === 0) return { ...response, truncated: true };
      return new Promise((resolve) => { resolveNext = resolve; });
    });
    render(<MemoryRouter initialEntries={[`/search?source=logs&mode=${mode}&refresh=0`]}><SearchPage /></MemoryRouter>);
    await screen.findByText('数据库连接超时');
    await waitFor(() => expect(observers.length).toBeGreaterThan(0));
    const first = vi.mocked(searchSql).mock.calls.find(([request]) => !request.sql.includes('GROUP BY'))![0];
    const histogramCount = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('GROUP BY')).length;
    fireEvent.change(screen.getByTestId(mode === 'sql' ? 'search-where-sql' : 'search-where'), { target: { value: 'unexecuted' } });
    intersect();
    intersect();
    const requests = vi.mocked(searchSql).mock.calls.filter(([request]) => !request.sql.includes('GROUP BY'));
    expect(requests).toHaveLength(2);
    expect(requests[1][0]).toEqual({ ...first, parameters: { ...first.parameters, page_offset: 1 } });
    expect(screen.queryByText('已显示全部结果')).toBeNull();
    await waitFor(() => expect(resolveNext).toBeTypeOf('function'));
    await act(async () => resolveNext({ columns: ['body'], rows: [['next-page']], elapsedMs: 1, truncated: false }));
    expect(screen.getByText('数据库连接超时')).toBeTruthy();
    expect(screen.getByText('next-page')).toBeTruthy();
    expect(screen.getByText('已显示全部结果')).toBeTruthy();
    expect(vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('GROUP BY'))).toHaveLength(histogramCount);
    intersect();
    expect(vi.mocked(searchSql).mock.calls.filter(([request]) => !request.sql.includes('GROUP BY'))).toHaveLength(2);
  });

  it('续页失败保留结果，重试同一页', async () => {
    const defaultSearch = vi.mocked(searchSql).getMockImplementation()!;
    let attempts = 0;
    vi.mocked(searchSql).mockImplementation(async (request) => {
      const response = await defaultSearch(request);
      if (request.sql.includes('GROUP BY')) return response;
      if (request.parameters?.page_offset === 0) return { ...response, truncated: true };
      if (++attempts === 1) throw new Error('续页失败');
      return { ...response, rows: [['2026-11-16T10:00:00Z', 'api', 'INFO', 'retry-page', null]], truncated: false };
    });
    render(<MemoryRouter initialEntries={['/search?source=logs&refresh=0']}><SearchPage /></MemoryRouter>);
    await screen.findByText('数据库连接超时');
    await waitFor(() => expect(observers.length).toBeGreaterThan(0));
    intersect();
    await screen.findByText('续页失败');
    expect(screen.getByText('数据库连接超时')).toBeTruthy();
    expect(screen.queryByText('已显示全部结果')).toBeNull();
    fireEvent.click(screen.getByText('重试'));
    await screen.findByText('retry-page');
    const pages = vi.mocked(searchSql).mock.calls.filter(([request]) => request.parameters?.page_offset === 1);
    expect(pages).toHaveLength(2);
    expect(pages[0][0]).toEqual(pages[1][0]);
  });

  it('重新查询后丢弃旧续页响应并重置offset', async () => {
    const defaultSearch = vi.mocked(searchSql).getMockImplementation()!;
    let resolveOld!: (value: Awaited<ReturnType<typeof searchSql>>) => void;
    let firstQueries = 0;
    vi.mocked(searchSql).mockImplementation(async (request) => {
      const response = await defaultSearch(request);
      if (request.sql.includes('GROUP BY')) return response;
      if (request.parameters?.page_offset !== 0) return new Promise((resolve) => { resolveOld = resolve; });
      if (++firstQueries === 1) return { ...response, truncated: true };
      return { ...response, rows: [['2026-11-16T10:00:00Z', 'api', 'INFO', 'new-query', null]] };
    });
    render(<MemoryRouter initialEntries={['/search?source=logs&refresh=0']}><SearchPage /></MemoryRouter>);
    await screen.findByText('数据库连接超时');
    await waitFor(() => expect(observers.length).toBeGreaterThan(0));
    intersect();
    fireEvent.click(screen.getByTestId('search-run'));
    await screen.findByText('new-query');
    await act(async () => resolveOld({ columns: ['body'], rows: [['stale-page']], elapsedMs: 1, truncated: false }));
    expect(screen.queryByText('stale-page')).toBeNull();
    expect(screen.queryByText('数据库连接超时')).toBeNull();
    expect(screen.getByText('已显示全部结果')).toBeTruthy();
  });

  it.each([0, 99, 100])('返回 %i 行且无探测行时停止续页', async (count) => {
    vi.mocked(searchSql).mockResolvedValue({
      columns: ['body'], rows: Array.from({ length: count }, (_, i) => [`row-${i}`]), elapsedMs: 1, truncated: false,
    });
    render(<MemoryRouter initialEntries={['/search?source=logs&limit=100&refresh=0']}><SearchPage /></MemoryRouter>);
    if (count > 0) {
      expect(await screen.findByText('已显示全部结果')).toBeTruthy();
    } else {
      expect(await screen.findByText('未找到匹配结果')).toBeTruthy();
    }
    expect(observers).toHaveLength(0);
  });

  it('未截断时不显示截断提示', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    expect(await screen.findByText('数据库连接超时')).toBeTruthy();
    expect(screen.queryByText(/结果已按行数上限截断/)).toBeNull();
  });

  it('直方图服务端全量聚合：独立 GROUP BY 查询 + 精确总数（不受行数上限影响）', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    const histogramCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('GROUP BY'));
    expect(histogramCalls.length).toBeGreaterThan(0);
    const request = histogramCalls[0][0];
    // 聚合查询复用相同 WHERE（{{logs}} 占位符 + $start/$end），分桶步长经 $bucket_ms 参数绑定；
    // 绑定值为 DOUBLE，须显式 floor() 保证桶对齐
    expect(request.sql).toContain('FROM {{logs}}');
    expect(request.sql).toContain('floor(epoch_ms(ts) / $bucket_ms)');
    expect(request.sql).toContain('GROUP BY 1, 2');
    expect(request.parameters).toEqual({ bucket_ms: 10_000 });
    expect(request.rowLimit).toBe(10_000);
    // 结果头部计数为聚合总数（2000+1838），而非返回行数
    await waitFor(() => {
      expect(screen.getByTestId('search-result-count').textContent).toContain('3,838');
    });
  });

  it('Lucene 说明跟随编辑条件，展开收起不改变输入或运行查询', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await screen.findByText('数据库连接超时');
    const callsBefore = vi.mocked(searchSql).mock.calls.length;
    expect(screen.getByTestId('search-lucene-help')).toBeTruthy();
    const input = screen.getByTestId('search-where') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'service:api timeout' } });
    expect(screen.getByTestId('search-lucene-help').textContent).toContain('service:api timeout');
    const toggle = screen.getByRole('button', { name: '使用说明' });
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(input.value).toBe('service:api timeout');
    expect(vi.mocked(searchSql).mock.calls.length).toBe(callsBefore);
  });

  it('属性表达式编译为命名参数查询', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    fireEvent.change(screen.getByTestId('search-where'), { target: { value: 'severity:ERROR timeout' } });
    fireEvent.click(screen.getByTestId('search-run'));
    await waitFor(() => {
      expectLastRequest(['severity = $p0', 'body LIKE $p1'], { p0: 'ERROR', p1: '%timeout%' });
    });
  });

  it('未知字段编译错误直接展示且不发起请求', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    const callsBefore = vi.mocked(searchSql).mock.calls.length;
    fireEvent.change(screen.getByTestId('search-where'), { target: { value: 'nope:1' } });
    fireEvent.click(screen.getByTestId('search-run'));
    expect(await screen.findByText(/未知字段/)).toBeTruthy();
    expect(vi.mocked(searchSql).mock.calls.length).toBe(callsBefore);
  });

  it('SQL 模式：行2 手写 WHERE 片段与行1 SELECT/ORDER BY 组装为完整查询', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs&mode=sql']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    // 首查为默认条件（无 WHERE 片段）：时间窗 + 行1 关键字条 + 行数上限
    expectLastRequest(['WHERE ts >= $start', 'ORDER BY ts DESC', 'LIMIT CAST($page_limit AS BIGINT)']);
    // 行1 关键字条不换位（两种语法同构），行2 为手写 WHERE 输入框（Lucene 搜索栏卸载）
    expect(screen.getByTestId('search-fields')).toBeTruthy();
    expect(screen.getByTestId('search-order-by')).toBeTruthy();
    expect(screen.getByTestId('search-where-sql')).toBeTruthy();
    expect(screen.queryByTestId('search-where')).toBeNull();
    expect(screen.queryByTestId('search-lucene-help')).toBeNull();
    fireEvent.change(screen.getByTestId('search-where-sql'), { target: { value: "severity = 'ERROR'" } });
    fireEvent.click(screen.getByTestId('search-run'));
    await waitFor(() => {
      expectLastRequest([
        'SELECT ts, service, severity, body, trace_id',
        "WHERE ts >= $start AND ts < $end AND (severity = 'ERROR')",
        'ORDER BY ts DESC',
      ]);
    });
  });

  it('SQL 模式旧地址（整句 sql 参数）：回退提取 WHERE 主体为手写片段', async () => {
    const legacySql = "SELECT ts, service FROM {{logs}} WHERE ts >= $start AND ts < $end AND severity = 'ERROR' ORDER BY ts DESC LIMIT 200";
    render(
      <MemoryRouter initialEntries={[`/search?source=logs&mode=sql&sql=${encodeURIComponent(legacySql)}`]}>
        <SearchPage />
      </MemoryRouter>,
    );
    // 首查即按提取出的 WHERE 片段组装（时间窗样板剥离，片段原样嵌入；行数取默认 500 不随旧句 LIMIT）
    await waitFor(() => {
      expectLastRequest(["WHERE ts >= $start AND ts < $end AND (severity = 'ERROR')", 'ORDER BY ts DESC', 'LIMIT CAST($page_limit AS BIGINT)']);
    });
    expect((screen.getByTestId('search-where-sql') as HTMLInputElement).value).toBe("severity = 'ERROR'");
  });

  it('traces 数据源：查询切换为链路模板并渲染动态列', async () => {
    vi.mocked(searchSql).mockResolvedValue({
      columns: ['ts', 'service', 'name', 'duration_us', 'status', 'trace_id'],
      rows: [['2026-11-16T10:00:01Z', 'api-server', 'GET /api/users', 150_000, 'ok', 'abc123']],
      elapsedMs: 5,
      truncated: false,
    });
    render(
      <MemoryRouter initialEntries={['/search?source=traces']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    expectLastRequest(['FROM {{traces}}', 'ORDER BY ts DESC']);
    await waitFor(() => {
      expect(screen.getByText('GET /api/users')).toBeTruthy();
    });
    // duration_us 格式化为 ms（150000µs → 150.0 ms）
    expect(screen.getAllByText('150.0 ms').length).toBeGreaterThan(0);
  });

  it('列选择浮层：取消勾选列即时重查并更新 SELECT', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    // 打开 SELECT 关键字条浮层，取消 service 列
    fireEvent.click(screen.getByTestId('search-fields'));
    const serviceCheckbox = await screen.findByRole('checkbox', { name: 'service' });
    fireEvent.click(serviceCheckbox);
    await waitFor(() => {
      expectLastRequest(['SELECT ts, severity, body, trace_id']);
    });
  });

  it('行首箭头就地展开明细，行点击打开详情抽屉（概览分区）', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(screen.getByText('数据库连接超时')).toBeTruthy();
    });
    // 未展开时无按值过滤按钮
    expect(screen.queryAllByRole('button', { name: '按此值过滤' }).length).toBe(0);
    // 点击行首箭头 → 就地展开全字段明细（不打开抽屉，默认字段模式）
    fireEvent.click(screen.getByRole('button', { name: '行明细' }));
    expect(screen.getAllByRole('button', { name: '按此值过滤' }).length).toBeGreaterThan(0);
    // 明细中包含 trace_id 字段行（表头之外多出一处）
    expect(screen.getAllByText('trace_id').length).toBeGreaterThan(1);
    expect(screen.queryByTestId('log-detail-drawer')).toBeNull();
    // JSON 预览：整行对象递归树（叶子字符串带引号，无按值过滤按钮）
    fireEvent.click(screen.getByRole('button', { name: 'JSON' }));
    expect(screen.getByText('"数据库连接超时"')).toBeTruthy();
    expect(screen.queryAllByRole('button', { name: '按此值过滤' }).length).toBe(0);
    // 原文预览：body 不做任何解析直接展示（无 JSON 引号包装）
    fireEvent.click(screen.getByRole('button', { name: '原文' }));
    expect(screen.queryByText('"数据库连接超时"')).toBeNull();
    expect(screen.getAllByText('数据库连接超时').length).toBeGreaterThan(0);
    // 点击行 → 打开详情抽屉（身份补查完整记录后渲染概览分区）
    fireEvent.click(screen.getAllByText('数据库连接超时')[0]);
    expect(await screen.findByTestId('log-detail-drawer')).toBeTruthy();
    expect(await screen.findByText('消息体')).toBeTruthy();
    expect(screen.getByText('顶层属性')).toBeTruthy();
    expect(screen.getByText('日志属性')).toBeTruthy();
    expect(screen.getByText('资源属性')).toBeTruthy();
  });

  it('日志域点 trace_id 深链跳转链路视图（URL 携带 traceId 参数并按其过滤查询）', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(screen.getByText('数据库连接超时')).toBeTruthy();
    });
    // 点击 trace_id 单元格 → URL 切换为 source=traces&traceId=... 并按该链路过滤查询
    fireEvent.click(screen.getByText('abc123def456…'));
    await waitFor(() => {
      expectLastRequest(['FROM {{traces}}', 'trace_id = $p0'], { p0: 'abc123def4567890' });
    });
  });

  it('已保存搜索上下文（savedId）：打开即恢复快照条件，保存即更新当前条目', async () => {
    render(
      <MemoryRouter initialEntries={['/saved-searches/2']}>
        <SearchPage savedId={2} />
      </MemoryRouter>,
    );
    // 快照加载并恢复查询状态（where/fields/limit），随即以恢复完的条件自动重查
    await waitFor(() => {
      expect(getSavedQuery).toHaveBeenCalledWith(2);
    });
    await waitFor(() => {
      expect((screen.getByTestId('search-where') as HTMLInputElement).value).toBe('severity:ERROR timeout');
    });
    await waitFor(() => {
      expectLastRequest(['SELECT ts, service', 'FROM {{logs}}', 'ORDER BY ts DESC', 'LIMIT CAST($page_limit AS BIGINT)'], { p0: 'ERROR', p1: '%timeout%' });
    });
    // 保存弹层中「保存当前搜索」替换为「更新当前搜索」
    fireEvent.click(screen.getByTestId('search-saved-button'));
    fireEvent.click(await screen.findByText('更新当前搜索'));
    // 保存 = 覆盖更新当前条目（携带 id，不弹新建命名弹窗）
    await waitFor(() => {
      expect(saveQuery).toHaveBeenCalledWith(expect.objectContaining({ id: 2, name: '慢链路', kind: 'search' }));
    });
  });

  it('URL 条件恢复：带参地址打开即按 q/limit/range 恢复条件并自动执行首查', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs&q=severity%3AERROR%20timeout&limit=200&range=1h']}>
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    // 查询语句/行数按地址恢复编译进 SQL
    const request = expectLastRequest(['FROM {{logs}}', 'LIMIT CAST($page_limit AS BIGINT)'], { p0: 'ERROR', p1: '%timeout%' });
    // range=1h → 查询窗口为最近 1 小时（from/to 相差约 3600s）
    const spanMs = dayjs(request.to).diff(dayjs(request.from));
    expect(spanMs).toBeGreaterThanOrEqual(3_590_000);
    expect(spanMs).toBeLessThanOrEqual(3_610_000);
    // 搜索栏回填恢复的查询语句
    expect((screen.getByTestId('search-where') as HTMLInputElement).value).toBe('severity:ERROR timeout');
  });

  it('URL 条件回写：执行查询后地址栏携带全部非默认条件（默认值省略）', async () => {
    const searches: string[] = [];
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <LocationProbe onChange={(search) => searches.push(search)} />
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect(searchSql).toHaveBeenCalled();
    });
    // 默认条件地址仅含 source（省略默认值）
    await waitFor(() => {
      expect(searches[searches.length - 1]).toBe('?source=logs');
    });
    fireEvent.change(screen.getByTestId('search-where'), { target: { value: 'service:api' } });
    fireEvent.click(screen.getByTestId('search-run'));
    // 运行后查询语句回写地址（URL 编码）
    await waitFor(() => {
      expect(searches.some((search) => search.includes('q=service%3Aapi'))).toBe(true);
    });
  });

  it('traceId 深链透传：落地为过滤器查询后地址仍保留 traceId 参数', async () => {
    const searches: string[] = [];
    render(
      <MemoryRouter initialEntries={['/search?source=traces&traceId=abc123']}>
        <LocationProbe onChange={(search) => searches.push(search)} />
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expectLastRequest(['FROM {{traces}}', 'trace_id = $p0'], { p0: 'abc123' });
    });
    // trace_id 过滤物化为 filters 的同时，traceId 深链参数原样保留（浏览器回退语义不变）
    await waitFor(() => {
      expect(searches.some((search) => search.includes('traceId=abc123') && search.includes('filters='))).toBe(true);
    });
  });

  it('savedId 上下文不同步 URL：快照恢复不改写地址栏', async () => {
    const searches: string[] = [];
    render(
      <MemoryRouter initialEntries={['/saved-searches/2']}>
        <LocationProbe onChange={(search) => searches.push(search)} />
        <SearchPage savedId={2} />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expect((screen.getByTestId('search-where') as HTMLInputElement).value).toBe('severity:ERROR timeout');
    });
    await waitFor(() => {
      expectLastRequest(['LIMIT CAST($page_limit AS BIGINT)'], { p0: 'ERROR', p1: '%timeout%' });
    });
    // 快照条件落地后地址保持路由原始形态（无条件参数写入）
    expect(searches.every((search) => search === '')).toBe(true);
  });

  it('浏览器回退跨域还原：上一域默认字段/排序跟随新域、trace 过滤器清空，不产生 Unknown field name', async () => {
    render(
      <MemoryRouter initialEntries={['/search?source=logs', '/search?source=traces&traceId=abc123']} initialIndex={1}>
        <BackButton />
        <SearchPage />
      </MemoryRouter>,
    );
    // 深链落地：traces 默认列 + trace_id 过滤器查询
    await waitFor(() => {
      expectLastRequest(['FROM {{traces}}', 'trace_id = $p0'], { p0: 'abc123' });
    });
    // 浏览器返回 → URL 还原日志视图（回退只还原 URL 不还原状态），渲染期域适配让
    // 默认字段/排序跟随 logs、trace 过滤器清空，随后以 logs 条件重查（修复前此处报 Unknown field name）
    fireEvent.click(screen.getByTestId('test-back'));
    await waitFor(() => {
      expectLastRequest(['SELECT ts, service, severity, body, trace_id', 'FROM {{logs}}']);
    });
    expect(screen.queryByText(/未知字段/)).toBeNull();
  });

  /** 探针按钮：模拟数据源选择器 switchSource 的路由动作（整 URL 以 source 参数替换）. */
  function SwitchTracesButton() {
    const navigate = useNavigate();
    return <button type="button" onClick={() => navigate('/search?source=traces', { replace: true })} data-testid="test-switch-traces">switch</button>;
  }

  it('SQL 模式跨域切换：自定义字段/排序无条件重置为新域默认，不再残留 logs 字段报 Unknown field', async () => {
    const customFields = encodeURIComponent('ts, service, severity, body, trace_id, attributes["order.no"]');
    const searches: string[] = [];
    render(
      <MemoryRouter initialEntries={[`/search?source=logs&mode=sql&fields=${customFields}`]}>
        <LocationProbe onChange={(search) => searches.push(search)} />
        <SwitchTracesButton />
        <SearchPage />
      </MemoryRouter>,
    );
    // 初始以 logs 自定义字段（含 severity/body/JSON 列）执行 SQL 模式查询
    await waitFor(() => {
      expectLastRequest(['FROM {{logs}}']);
    });
    // 切换数据源 → traces：字段/排序无条件重置为新域默认（修复前 SQL 模式跳过适配，
    // logs 字段跨域残留，编译报「未知字段 severity」横幅且无结果）
    fireEvent.click(screen.getByTestId('test-switch-traces'));
    await waitFor(() => {
      expectLastRequest(['SELECT ts, service, name, duration_us, status, trace_id', 'FROM {{traces}}']);
    });
    expect(screen.queryByText(/未知字段/)).toBeNull();
    // 地址栏不再残留旧域 fields 参数（字段等于 traces 默认值即省略）
    const params = new URLSearchParams(searches[searches.length - 1]);
    expect(params.get('source')).toBe('traces');
    expect(params.get('fields')).toBeNull();
  });

  it('Lucene 模式跨域切换：已自定义的字段/排序同样重置为新域默认（跨域字段列表没有可移植子集）', async () => {
    const customFields = encodeURIComponent('ts, service, severity, body');
    render(
      <MemoryRouter initialEntries={[`/search?source=logs&fields=${customFields}`]}>
        <SwitchTracesButton />
        <SearchPage />
      </MemoryRouter>,
    );
    await waitFor(() => {
      expectLastRequest(['FROM {{logs}}']);
    });
    fireEvent.click(screen.getByTestId('test-switch-traces'));
    await waitFor(() => {
      expectLastRequest(['SELECT ts, service, name, duration_us, status, trace_id', 'FROM {{traces}}']);
    });
    expect(screen.queryByText(/未知字段/)).toBeNull();
  });

  it('SELECT JSON 属性发现树：逐层展开子属性并勾选为结果列，查询带列别名与绑定提取路径', async () => {
    // 发现树子属性枚举（json_extract 取节点 + json_keys 枚举）返回 http key（facet 查询同样含 GROUP BY，须先于直方图分支）
    vi.mocked(searchSql).mockImplementation(async (request) => {
      if (request.sql.includes('json_keys') && request.sql.includes('json_extract(')) {
        return { columns: ['v', 'c'], rows: [['http', 5]], elapsedMs: 1, truncated: false };
      }

      if (request.sql.includes('GROUP BY')) {
        return { columns: ['bucket', 'grp', 'cnt'], rows: [[Date.now() - 60_000, 'ERROR', 1]], elapsedMs: 1, truncated: false };
      }

      return {
        columns: ['ts', 'service', 'severity', 'body', 'trace_id'],
        rows: [['2026-11-16T10:00:01Z', 'api-server', 'ERROR', '数据库连接超时', 'abc123def4567890']],
        elapsedMs: 12,
        truncated: false,
      };
    });
    render(
      <MemoryRouter initialEntries={['/search?source=logs']}>
        <SearchPage />
      </MemoryRouter>,
    );
    // 首查完成（searchedRange 就绪，发现树可用）
    await waitFor(() => {
      expectLastRequest(['FROM {{logs}}']);
    });
    // 打开显示列浮层 → 展开 attributes → 勾选子属性 http（复选框 aria-label 为完整括号路径）
    fireEvent.click(screen.getByTestId('search-fields'));
    fireEvent.click(await screen.findByTestId('search-json-node-attributes'));
    fireEvent.click(await screen.findByLabelText('attributes["http"]'));
    await waitFor(() => {
      const call = vi.mocked(searchSql).mock.calls
        .map(([request]) => request)
        .find((request) => request.sql.includes('AS "attributes[""http""]"'));
      expect(call).toBeTruthy();
      expect(call!.sql).toContain('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END,');
      expect(Object.values(call!.parameters ?? {})).toContain('$."http"');
    });
  });
});
