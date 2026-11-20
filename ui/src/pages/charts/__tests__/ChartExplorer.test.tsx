import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { MemoryRouter, useLocation, useNavigate } from 'react-router';
import { CHART_URL_TABS, parseChartUrlState, serializeChartUrlState, type ChartUrlState } from '../chartUrlState';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import '@/i18n';
// jsdom 无 canvas，mock echarts 封装组件与 SQL 编辑器
vi.mock('@/design-system/Chart', () => ({ Chart: ({ onEvents }: { onEvents?: Record<string, (event: unknown) => void> }) => <div data-testid="mock-chart"><button onClick={() => onEvents?.datazoom({ start: 25, end: 75 })}>缩小时间范围</button></div> }));
vi.mock('@/pages/search/SqlEditor', () => ({
  SqlEditor: ({ value, readOnly, height, onChange, onRun }: { value: string; readOnly?: boolean; height?: string; onChange?: (value: string) => void; onRun?: () => void }) => readOnly
    ? <div data-testid="mock-sql-editor" data-readonly={readOnly}>{value}</div>
    : <textarea data-testid="mock-sql-editor" data-height={height} value={value} onChange={(event) => onChange?.(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) onRun?.(); }} />,
}));
vi.mock('@/pages/search/LogDetailDrawer', () => ({ LogDetailDrawer: ({ row, onClose }: { row: Record<string, unknown>; onClose: () => void }) => <div role="dialog">{String(row.body)}<button onClick={onClose}>关闭详情</button></div> }));
import { ChartExplorerPage } from '../ChartExplorerPage';
import { ChartWorkspace } from '../ChartWorkspace';
import { ChartWorkspaceContext } from '../ChartWorkspaceContext';
import { chartToPanel, type ChartPanelInput } from '../chartPanel';
import { searchSql } from '@/api/search';
import { useAuthStore } from '@/store/auth';

const chartResponse = { columns: ['ts_bucket', 'value'], rows: [['2026-11-16T10:00:00Z', 3], ['2026-11-16T10:05:00Z', 5]], elapsedMs: 12, truncated: false };
const sampleResponse = { columns: ['ts', 'service', 'severity', 'body', 'trace_id'], rows: [['2026-11-16T10:00:01Z', 'api-server', 'ERROR', "boom 'quoted'", 'abc123']], elapsedMs: 8, truncated: false };
vi.mock('@/api/search', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/search')>()), searchSql: vi.fn(),
}));

function UrlProbe() {
  const location = useLocation();
  const navigate = useNavigate();
  return <><output data-testid="chart-url">{location.search}</output><button onClick={() => navigate('/charts?tab=patterns')}>导航到模式链接</button><button onClick={() => navigate(-1)}>返回链接</button></>;
}
function urlState() {
  return parseChartUrlState(new URLSearchParams(screen.getByTestId('chart-url').textContent ?? ''));
}
function mountUrl(entry = '/charts') {
  return render(<MemoryRouter initialEntries={[entry]}><UrlProbe /><ChartExplorerPage /></MemoryRouter>);
}

function mount() {
  return render(<MemoryRouter initialEntries={['/charts']}><ChartExplorerPage /></MemoryRouter>);
}
async function ready() {
  await waitFor(() => expect(screen.getByTestId('chart-run')).not.toBeDisabled());
}

describe('ChartExplorerPage', () => {
  beforeEach(() => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    vi.mocked(searchSql).mockReset().mockImplementation(async ({ sql }) => sql.includes('ORDER BY ts DESC') ? sampleResponse : chartResponse);
    useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
  });

  afterEach(() => { vi.useRealTimers(); });

  it('渲染 HyperDX 式 Builder 布局并自动运行', async () => {
    render(
      <MemoryRouter initialEntries={['/charts']}>
        <ChartExplorerPage />
      </MemoryRouter>,
    );
    // 页签：默认激活时序图，全部页签可见
    expect(screen.getByText('时序图')).toBeTruthy();
    expect(screen.getByText('热力图')).toBeTruthy();
    // Builder 行：图表名称/数据源/条件/分组/添加序列/显示设置/运行
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByText('图表名称')).toBeTruthy();
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByText('数据源')).toBeTruthy();
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByText('条件')).toBeTruthy();
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByText('分组')).toBeTruthy();
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByText('添加序列')).toBeTruthy();
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByText('显示设置')).toBeTruthy();
    expect(screen.getByTestId('chart-run')).toBeTruthy();
    // 首次进入自动运行：事件计数序列图
    await waitFor(() => {
      expect(screen.getByTestId('mock-chart')).toBeTruthy();
    });
    // 折叠区：生成的 SQL
    fireEvent.click(screen.getByRole('button', { name: '生成的 SQL' }));
    await waitFor(() => {
      expect(screen.getByText(/time_bucket/)).toBeTruthy();
    });
  });

  it('切换表格使用独立编辑器与无时间桶查询', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByRole('tab', { name: '表格' }));
    await waitFor(() => expect(screen.getByTestId('table-run')).not.toBeDisabled());
    expect(screen.getByTestId('table-explorer')).toBeVisible();
    expect(screen.getByTestId('chart-aggregate-editor')).toHaveClass('hidden');
    const request = vi.mocked(searchSql).mock.calls.find(([item]) => item.sql.includes('WITH inputs'))![0];
    expect(request.sql).not.toContain('time_bucket');
    expect(request.rowLimit).toBe(11);
  });

  it('页签切换后渲染跟随激活页签，不残留上一页签数据', async () => {
    mount(); await ready();
    // 时序图 → 数值：立即按数值形态重查并渲染数值卡，图表消失
    fireEvent.click(screen.getByRole('tab', { name: '数值' }));
    await waitFor(() => expect(screen.getByText('3')).toBeTruthy());
    expect(screen.queryByTestId('mock-chart')).not.toBeInTheDocument();
    // 数值 → 时序图：立即重查并渲染图表，上一页签的大数不再残留
    fireEvent.click(screen.getByRole('tab', { name: '时序图' }));
    await waitFor(() => expect(screen.getByTestId('mock-chart')).toBeTruthy());
    expect(screen.queryByText('3')).toBeNull();
    expect(screen.queryByText('5')).toBeNull();
    // 最近一次图表查询按激活页签（time_series）的 SQL 形态生成
    const chartCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => !request.sql.includes('ORDER BY ts DESC'));
    expect(chartCalls.length).toBeGreaterThanOrEqual(2);
    expect(chartCalls[chartCalls.length - 1]![0].sql).toContain('time_bucket');
  });

  it('切换 SQL 模式显示编辑器并执行用户 SQL', async () => {
    vi.mocked(searchSql).mockResolvedValue({
      columns: ['value'],
      rows: [[42]],
      elapsedMs: 3,
      truncated: false,
    });
    render(
      <MemoryRouter initialEntries={['/charts']}>
        <ChartExplorerPage />
      </MemoryRouter>,
    );
    // 切到 SQL 模式（模式切换按钮，避开条件下拉里的 SQL 选项文本）
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    await waitFor(() => {
      expect(screen.getByTestId('mock-sql-editor')).toBeTruthy();
    });
    // 切到数值图表类型并运行 → 数值卡渲染 42
    fireEvent.click(screen.getByText('数值'));
    fireEvent.click(screen.getByTestId('chart-run'));
    await waitFor(() => {
      expect(screen.getByText('42')).toBeTruthy();
    });
  });
  it('时序 SQL 工作区说明可折叠和复制，隐藏 Builder 控件且设置不重查', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: '添加公式' }));
    fireEvent.change(screen.getByRole('textbox', { name: '公式表达式 1' }), { target: { value: 'A * 2' } });
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    expect(screen.getByRole('button', { name: 'SQL' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByTestId('mock-sql-editor')).toHaveAttribute('data-height', '260px');
    expect(screen.queryByRole('button', { name: '添加公式' })).toBeNull();
    expect(screen.queryByRole('button', { name: '添加序列' })).toBeNull();
    expect(screen.queryByRole('switch', { name: '显示输入序列' })).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'granularity' })).toBeNull();
    const instructions = screen.getByRole('button', { name: 'SQL 图表说明' });
    expect(instructions).toHaveAttribute('aria-expanded', 'true');
    fireEvent.click(screen.getByRole('button', { name: '复制示例' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expect.stringContaining('WHERE ts >= $start AND ts < $end')));
    fireEvent.click(instructions);
    expect(instructions).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('button', { name: '复制示例' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '显示设置' }));
    fireEvent.click(screen.getByRole('switch', { name: '显示图例' }));
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls);
    fireEvent.keyDown(screen.getByRole('switch', { name: '显示图例' }), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Builder' }));
    expect(screen.getByRole('textbox', { name: '公式表达式 1' })).toHaveValue('A * 2');
  });

  it('SQL 草稿跨模式保留，编辑后快捷键运行并保留绑定参数', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByRole('button', { name: '行明细' }));
    fireEvent.click(screen.getAllByRole('button', { name: '按此值过滤' })[2]);
    await ready();
    const builderRequest = vi.mocked(searchSql).mock.calls.slice(-2)[0][0];
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    const sql = builderRequest.sql + '\n-- edited draft';
    fireEvent.change(screen.getByTestId('mock-sql-editor'), { target: { value: sql } });
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Builder' }));
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    expect(screen.getByTestId('mock-sql-editor')).toHaveValue(sql);
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls);
    fireEvent.keyDown(screen.getByTestId('mock-sql-editor'), { key: 'Enter', ctrlKey: true });
    await ready();
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls + 1);
    expect(vi.mocked(searchSql).mock.lastCall?.[0]).toEqual(expect.objectContaining({ sql, parameters: builderRequest.parameters }));
  });

  it('生成 SQL 展示及复制实际条件值，执行仍保留绑定参数', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    mount(); await ready();
    fireEvent.change(screen.getByTestId('chart-where-0'), { target: { value: "service = 'api-gateway'" } });
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    const request = vi.mocked(searchSql).mock.calls.slice(-2)[0][0];
    expect(request.sql).toContain('$cf0');
    expect(request.sql).not.toContain("'api-gateway'");
    expect(request.parameters).toMatchObject({ cf0: 'api-gateway' });
    fireEvent.click(screen.getByRole('button', { name: '生成的 SQL' }));
    const panel = document.getElementById('chart-generated-sql')!;
    expect(within(panel).getByTestId('mock-sql-editor')).toHaveTextContent("service = 'api-gateway'");
    expect(within(panel).getByTestId('mock-sql-editor')).not.toHaveTextContent('$cf0');
    fireEvent.click(within(panel).getByRole('button', { name: '复制生成的 SQL' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expect.stringContaining("service = 'api-gateway'")));
  });

  it('生成 SQL 使用只读高亮组件并支持复制', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: '生成的 SQL' }));
    expect(screen.getByTestId('mock-sql-editor')).toHaveAttribute('data-readonly', 'true');
    const panel = document.getElementById('chart-generated-sql')!;
    fireEvent.click(within(panel).getByRole('button', { name: '复制生成的 SQL' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expect.stringContaining('$start')));
  });

  it.each(['chart-source-actions', 'chart-actions'])('操作菜单 %s 先预览高亮 SQL，确认后才复制', async (trigger) => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    mount(); await ready();
    fireEvent.keyDown(screen.getByTestId(trigger), { key: 'Enter' });
    expect(screen.queryByRole('menuitem', { name: '复制生成的 SQL' })).toBeNull();
    fireEvent.click(await screen.findByRole('menuitem', { name: '预览 SQL' }));
    const dialog = await screen.findByRole('dialog', { name: '预览 SQL' });
    expect(within(dialog).getByTestId('mock-sql-editor')).toHaveAttribute('data-readonly', 'true');
    expect(within(dialog).getByTestId('mock-sql-editor')).toHaveTextContent('time_bucket');
    expect(writeText).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: '复制生成的 SQL' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expect.stringContaining('$start')));
    fireEvent.click(within(dialog).getByRole('button', { name: 'close' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: '预览 SQL' })).toBeNull());
  });

  it('箭头仅展开日志，点击正文打开侧栏，字段过滤使用绑定参数', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByRole('button', { name: '行明细' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByText('JSON')).toBeTruthy();
    const logRow = screen.getByTitle("boom 'quoted'").closest('tr')!;
    fireEvent.click(logRow);
    expect(screen.getByRole('dialog')).toHaveTextContent("boom 'quoted'");
    fireEvent.click(screen.getByRole('button', { name: '关闭详情' }));
    const filterButtons = screen.getAllByRole('button', { name: '按此值过滤' });
    fireEvent.click(filterButtons[2]);
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    const request = vi.mocked(searchSql).mock.calls.slice(-2)[0]![0];
    expect(request.parameters).toEqual(expect.objectContaining({ p0: "boom 'quoted'" }));
    expect(request.sql).not.toContain("boom 'quoted'");
  });

  it('时序与数值的 Lucene 说明按序列展示，折叠不查询且完整 SQL 隐藏', async () => {
    mount(); await ready();
    expect(within(screen.getByTestId('chart-aggregate-editor')).queryByTestId('chart-lucene-help')).toBeNull();
    fireEvent.keyDown(screen.getByRole('combobox', { name: '条件语法 1' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^Lucene$/ }));
    fireEvent.change(screen.getByTestId('chart-where-0'), { target: { value: 'service:api' } });
    expect(within(screen.getByTestId('chart-aggregate-editor')).getByTestId('chart-lucene-help')).toHaveTextContent('service:api');
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(within(within(screen.getByTestId('chart-aggregate-editor')).getByTestId('chart-lucene-help')).getByRole('button', { name: '使用说明' }));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.click(screen.getByRole('button', { name: '复制序列' }));
    const helps = within(screen.getByTestId('chart-aggregate-editor')).getAllByTestId('chart-lucene-help');
    expect(helps).toHaveLength(2);
    fireEvent.change(screen.getByTestId('chart-where-1'), { target: { value: 'service:web' } });
    expect(helps[0]).toHaveTextContent('service:api');
    expect(helps[1]).toHaveTextContent('service:web');
    fireEvent.click(screen.getByRole('tab', { name: '数值' })); await ready();
    expect(within(screen.getByTestId('chart-aggregate-editor')).getAllByTestId('chart-lucene-help')).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: /^SQL$/ }));
    expect(within(screen.getByTestId('chart-aggregate-editor')).queryByTestId('chart-lucene-help')).toBeNull();
  });

  it('Number WHERE 支持 Lucene 参数绑定并在切换完整 SQL 时保留条件', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByRole('tab', { name: '数值' })); await ready();
    fireEvent.keyDown(screen.getByRole('combobox', { name: '条件语法 1' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^Lucene$/ }));
    const input = screen.getByTestId('chart-where-0');
    fireEvent.change(input, { target: { value: 'severity:ERROR' } });
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    const requests = vi.mocked(searchSql).mock.calls.slice(-2).map(([request]) => request);
    for (const request of requests) {
      expect(request.parameters).toEqual(expect.objectContaining({ p0: 'ERROR' }));
      expect(request.sql).toContain('severity = $p0');
      expect(request.sql).not.toContain('severity:ERROR');
    }
    expect(input).toHaveValue('severity:ERROR');
    fireEvent.click(screen.getByRole('button', { name: /^SQL$/ }));
    expect((screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement).value).toContain('severity = $p0');
  });

  it('WHERE 智能提示采纳不运行且无效 Lucene 阻止请求', async () => {
    mount(); await ready();
    const input = screen.getByTestId('chart-where-0');
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'sev' } });
    expect(await screen.findByRole('option', { name: /severity/ })).toBeTruthy();
    fireEvent.keyDown(input, { key: 'Tab' });
    expect(input).toHaveValue('severity ');
    expect(searchSql).toHaveBeenCalledTimes(calls);
    fireEvent.blur(input);
    fireEvent.keyDown(screen.getByRole('combobox', { name: '条件语法 1' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^Lucene$/ }));
    fireEvent.change(input, { target: { value: 'unknown:value' } });
    fireEvent.click(screen.getByTestId('chart-run'));
    expect(searchSql).toHaveBeenCalledTimes(calls);
    expect(screen.getByText(/未知字段 "unknown"/)).toBeTruthy();
  });

  it('自定义区间校验、固定时间与实际跨度自动粒度', async () => {
    mount(); await ready();
    fireEvent.click(screen.getByTestId('chart-time-range'));
    expect(screen.queryByText('实时追踪')).toBeNull();
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '2026-11-17 12:00' } });
    fireEvent.change(screen.getByLabelText('结束时间'), { target: { value: '2026-11-17 10:00' } });
    fireEvent.click(screen.getByRole('button', { name: '应用' }));
    expect(screen.getByLabelText('开始时间')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('开始时间'), { target: { value: '2026-11-15 10:00' } });
    fireEvent.click(screen.getByRole('button', { name: '应用' }));
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    const first = vi.mocked(searchSql).mock.calls.slice(-2)[0]![0];
    expect(first.sql).toContain("INTERVAL '30 minutes'");
    expect(new Date(first.to).getTime() - new Date(first.from).getTime()).toBe(48 * 3600000);
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    expect(vi.mocked(searchSql).mock.calls.slice(-2)[0]![0].from).toBe(first.from);
  });

  it('重复运行相对窗口会更新到当前时间', async () => {
    mount(); await ready();
    const previous = vi.mocked(searchSql).mock.calls[0][0];
    const date = vi.spyOn(Date, 'now').mockReturnValue(new Date(previous.to).getTime() + 60000);
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    date.mockRestore();
    const next = vi.mocked(searchSql).mock.calls.slice(-2)[0]![0];
    expect(new Date(next.to).getTime()).toBeGreaterThanOrEqual(new Date(previous.to).getTime());
    expect(new Date(next.to).getTime() - new Date(next.from).getTime()).toBe(3600000);
  });

  it('图表缩放同步时间范围并重查图表与样本', async () => {
    mount(); await ready();
    const first = vi.mocked(searchSql).mock.calls[0][0];
    fireEvent.click(screen.getByRole('button', { name: '缩小时间范围' }));
    await ready();
    const calls = vi.mocked(searchSql).mock.calls.slice(-2);
    expect(calls[0][0].from).toBe(calls[1][0].from);
    expect(calls[0][0].to).toBe(calls[1][0].to);
    expect(new Date(calls[0][0].to).getTime() - new Date(calls[0][0].from).getTime()).toBe(1800000);
    expect(new Date(calls[0][0].from).getTime()).toBeGreaterThan(new Date(first.from).getTime());
  });

  it('切源后尚未运行也能从 attributes 根继续发现子属性', async () => {
    mount(); await ready();
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /链路/ }));
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'domain' })).toHaveFocus());
    fireEvent.keyDown(screen.getByRole('combobox', { name: '条件语法 1' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /^Lucene$/ }));
    vi.mocked(searchSql).mockResolvedValue({ columns: ['v', 'c'], rows: [['http.method', 2]], elapsedMs: 1, truncated: false });
    const before = vi.mocked(searchSql).mock.calls.length;
    const input = screen.getByTestId('chart-where-0');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'attributes[' } });
    expect(await screen.findByRole('option', { name: /http.method/ })).toBeInTheDocument();
    const requests = vi.mocked(searchSql).mock.calls.slice(before);
    expect(requests.length).toBeGreaterThan(0);
    for (const [request] of requests) {
      expect(request.sql).toContain('json_keys');
      expect(request.sql).toContain('{{traces}}');
      expect(request.from).toBeTruthy();
      expect(request.to).toBeTruthy();
    }
    fireEvent.click(screen.getByRole('option', { name: /http.method/ }));
    expect(input).toHaveValue('attributes["http.method"]');
    expect(screen.getByRole('option', { name: /筛选此属性的值/ })).toBeInTheDocument();
  });

  it('五种来源切换后使用各自的真实查询', async () => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    mount(); await ready();
    for (const [label, token] of [['MQTT', 'iot'], ['指标', 'metrics'], ['客户端会话', 'sessions'], ['链路', 'traces'], ['日志', 'logs']]) {
      fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
      fireEvent.click(await screen.findByRole('option', { name: new RegExp(label) }));
      if (token === 'metrics') {
        await waitFor(() => expect(screen.getByRole('combobox', { name: 'domain' })).toHaveFocus());
        vi.mocked(searchSql).mockResolvedValueOnce({ columns: ['name', 'kinds'], rows: [['cpu_usage', 'gauge']], elapsedMs: 1, truncated: false });
        fireEvent.click(screen.getByTestId('metric-picker-0'));
        fireEvent.click(await screen.findByRole('option', { name: /cpu_usage/ }));
        await waitFor(() => expect(screen.getByTestId('metric-picker-0')).toHaveTextContent('cpu_usage'));
      }
      const before = vi.mocked(searchSql).mock.calls.length;
      fireEvent.click(screen.getByTestId('chart-run')); await ready();
      const calls = vi.mocked(searchSql).mock.calls.slice(before);
      expect(calls).toHaveLength(token === 'metrics' ? 1 : 2);
      for (const [request] of calls) expect(request.sql).toContain(`{{${token}}}`);
      if (token === 'metrics') expect(Object.values(calls[0][0].parameters!)).toContain('cpu_usage');
      if (token === 'iot') expect(calls[0][0].sql).toContain('avg("num_value")');
    }
  });

  it('SQL 四来源联动查询与示例，切回恢复各自草稿且不自动运行', async () => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    const logsDraft = String((screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement).value) + '\n-- logs draft';
    fireEvent.change(screen.getByTestId('mock-sql-editor'), { target: { value: logsDraft } });
    const calls = vi.mocked(searchSql).mock.calls.length;
    for (const [label, source, example] of [
      ['指标', 'metrics', '示例 · 每分钟各指标平均值'],
      ['客户端会话', 'sessions', '示例 · 每分钟各项目去重会话数'],
      ['链路', 'traces', '示例 · 每分钟各服务链路计数'],
    ]) {
      fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
      fireEvent.click(await screen.findByRole('option', { name: new RegExp(label) }));
      const editor = screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement;
      expect(editor.value).toContain('{{' + source + '}}');
      expect(editor.value).not.toContain('{{logs}}');
      expect(screen.getByText(example)).toBeInTheDocument();
      expect(screen.queryByTestId('mock-chart')).toBeNull();
      if (source === 'metrics') expect(editor.value).toContain('avg("value")');
      if (source === 'sessions') expect(editor.value).toContain('GROUP BY project_key, session_id');
      fireEvent.click(screen.getByRole('button', { name: '复制示例' }));
      await waitFor(() => expect(writeText).toHaveBeenLastCalledWith(expect.stringContaining('{{' + source + '}}')));
      fireEvent.change(editor, { target: { value: editor.value + '\n-- ' + source + ' draft' } });
    }
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls);
    for (const [label, ending] of [['日志', '-- logs draft'], ['指标', '-- metrics draft'], ['客户端会话', '-- sessions draft'], ['链路', '-- traces draft']]) {
      fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
      fireEvent.click(await screen.findByRole('option', { name: new RegExp(label) }));
      expect((screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement).value.endsWith(ending)).toBe(true);
    }
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls + 1);
    expect(vi.mocked(searchSql).mock.lastCall?.[0].sql).toContain('{{traces}}');
  });

  it('Builder 切源后未运行即进入 SQL 使用当前配置而非旧执行结果', async () => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    mount(); await ready();
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: '指标' }));
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    const editor = screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement;
    expect(editor.value).toContain('{{metrics}}');
    expect(editor.value).toContain('avg("value")');
    expect(editor.value).not.toContain('{{logs}}');
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls);
  });

  it('SQL 切源隔离参数并在切回后恢复原参数字典', async () => {
    HTMLElement.prototype.scrollIntoView = vi.fn();
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByRole('button', { name: '行明细' }));
    fireEvent.click(screen.getAllByRole('button', { name: '按此值过滤' })[2]);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
    const logsSql = (screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement).value;
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: '指标' }));
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    expect(vi.mocked(searchSql).mock.lastCall?.[0].parameters).toEqual({});
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'domain' }), { key: 'ArrowDown' });
    fireEvent.click(await screen.findByRole('option', { name: /日志/ }));
    expect(screen.getByTestId('mock-sql-editor')).toHaveValue(logsSql);
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    const request = vi.mocked(searchSql).mock.lastCall?.[0];
    expect(request?.parameters).toEqual(expect.objectContaining({ p0: "boom 'quoted'" }));
    expect(request?.sql).not.toContain("boom 'quoted'");
  });

  it('只读角色没有保存图表入口', async () => {
    useAuthStore.setState({ role: 'readonly' });
    mount(); await ready();
    fireEvent.keyDown(screen.getByTestId('chart-actions'), { key: 'Enter' });
    expect(screen.queryByRole('menuitem', { name: '保存到仪表盘' })).toBeNull();
    expect(await screen.findByRole('menuitem', { name: '预览 SQL' })).toBeInTheDocument();
  });

  it('公式校验阻止查询，运行后可隐藏输入序列并保留已执行公式', async () => {
    vi.mocked(searchSql).mockImplementation(async ({ sql }) => {
      if (sql.includes('ORDER BY ts DESC')) return sampleResponse;
      return { ...chartResponse, columns: ['__series_id', 'series', '__formula', 'value'], rows: [['s1', 'A', false, 20], ['s2', 'B', false, 30], ['f1', 'Ratio', true, 40]] };
    });
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: '添加序列' }));
    fireEvent.click(screen.getByRole('button', { name: '添加公式' }));
    expect(screen.getByText('表达式不能为空')).toBeInTheDocument();
    const calls = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByTestId('chart-run'));
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls);
    fireEvent.change(screen.getByRole('textbox', { name: '公式表达式 1' }), { target: { value: 'A / (A + B) * 100' } });
    fireEvent.change(screen.getByRole('textbox', { name: '公式别名 1' }), { target: { value: 'Ratio' } });
    fireEvent.click(screen.getByRole('tab', { name: '数值' }));
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    expect(screen.getByText('20')).toBeInTheDocument();
    expect(screen.getByText('30')).toBeInTheDocument();
    expect(screen.getByText('40')).toBeInTheDocument();
    const request = vi.mocked(searchSql).mock.calls.slice(-2)[0][0];
    expect(request.sql).toContain('NULLIF');
    expect(Object.values(request.parameters!)).toContain(100);
    const afterRun = vi.mocked(searchSql).mock.calls.length;
    fireEvent.click(screen.getByRole('switch', { name: '显示输入序列' }));
    expect(screen.queryByText('20')).toBeNull();
    expect(screen.queryByText('30')).toBeNull();
    expect(screen.getByText('40')).toBeInTheDocument();
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(afterRun);
    fireEvent.change(screen.getByRole('textbox', { name: '公式表达式 1' }), { target: { value: 'A / C' } });
    expect(screen.getByText('未知输入序列：C')).toBeInTheDocument();
    expect(screen.getByText('40')).toBeInTheDocument();
  });

  it('共享工作区无需 Router 即可渲染且面板模式不自动查询', () => {
    render(<ChartWorkspaceContext.Provider value={{ mode: 'panel' }}><ChartWorkspace /></ChartWorkspaceContext.Provider>);
    expect(screen.getByTestId('chart-run')).toBeInTheDocument();
    expect(searchSql).not.toHaveBeenCalled();
  });

  it('恢复图表定义并仅应用画布草稿，保留布局与仪表盘时间', async () => {
    const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' };
    const input: ChartPanelInput = { name: 'Existing', chartType: 'time_series', source: 'logs', series: [{ id: 'a', ref: 'A', aggFunc: 'count', field: '', where: 'severity:ERROR', whereSyntax: 'lucene', alias: 'Errors' }], formulas: [{ id: 'f', expression: 'A * 100', alias: 'Ratio' }], range, sql: 'SELECT 1', parameters: {}, display: 'line', autoGranularity: false, showLegend: false,
      editor: { version: 1, mode: 'builder', chartType: 'time_series', source: 'logs', series: [{ id: 'a', ref: 'A', aggFunc: 'count', field: '', where: 'severity:ERROR', whereSyntax: 'lucene', alias: 'Errors' }], formulas: [{ id: 'f', expression: 'A * 100', alias: 'Ratio' }], groupBy: ['service'], granularity: '5m' } };
    const panel = { ...chartToPanel(input), id: 'stable', layout: { x: 6, y: 4, w: 6, h: 3 } };
    const onApply = vi.fn();
    render(<MemoryRouter initialEntries={['/dashboards/42?tab=pie&state=invalid']}><UrlProbe /><ChartWorkspaceContext.Provider value={{ mode: 'panel', initial: panel, definition: input.editor, range, onApply }}><ChartWorkspace /></ChartWorkspaceContext.Provider></MemoryRouter>);
    expect(searchSql).not.toHaveBeenCalled();
    expect(screen.getByRole('textbox', { name: '公式表达式 1' })).toHaveValue('A * 100');
    expect(screen.getByRole('combobox', { name: 'granularity' })).toHaveTextContent('5m');
    expect(screen.queryByTestId('chart-time-range')).toBeNull();
    fireEvent.click(screen.getByTestId('chart-run')); await ready();
    expect(vi.mocked(searchSql).mock.calls[0][0]).toMatchObject({ from: new Date(range.from).toISOString(), to: new Date(range.to).toISOString() });
    fireEvent.click(screen.getByRole('button', { name: '应用到画布' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: '应用到画布' }));
    expect(screen.getByTestId('chart-url')).toHaveTextContent('?tab=pie&state=invalid');
    expect(onApply).toHaveBeenCalledOnce();
    expect(onApply.mock.calls[0][0]).toMatchObject({ id: 'stable', layout: panel.layout, explorer: { editor: input.editor } });
    fireEvent.click(screen.getByRole('button', { name: /^SQL$/ }));
    expect(screen.getByTestId('mock-sql-editor')).not.toHaveValue('SELECT 1');
    expect((screen.getByTestId('mock-sql-editor') as HTMLTextAreaElement).value).toContain('time_bucket');
  });

  it('全部 Tab 写回地址且切回保留草稿，编辑条件不额外查询', async () => {
    mountUrl(); await ready();
    const calls = vi.mocked(searchSql).mock.calls.length;
    const where = screen.getByTestId('chart-where-0');
    fireEvent.change(where, { target: { value: "service = '中文 & +'" } });
    await waitFor(() => expect(urlState().state?.editor.series[0].where).toBe("service = '中文 & +'"));
    expect(vi.mocked(searchSql).mock.calls).toHaveLength(calls);
    const labels = ['时序图', '表格', '数值', '柱状图', '饼图', '事件搜索', '热力图', '模式'];
    for (let i = 1; i < labels.length; i++) {
      fireEvent.click(screen.getByRole('tab', { name: labels[i] }));
      await waitFor(() => expect(urlState().state?.editor.chartType).toBe(CHART_URL_TABS[i]));
    }
    fireEvent.click(screen.getByRole('tab', { name: '时序图' }));
    await waitFor(() => expect(urlState().state?.editor.series[0].where).toBe("service = '中文 & +'"));
  });

  it('直接打开 SQL 地址恢复来源、参数及绝对时间后首查', async () => {
    const state: ChartUrlState = {
      editor: { version: 1, chartType: 'bar', mode: 'sql', source: 'metrics', series: [], formulas: [], groupBy: [], granularity: 'auto' },
      range: { from: '2026-11-18T00:00:00.000Z', to: '2026-11-18T02:00:00.000Z' },
      sql: 'SELECT $p0 AS value', parameters: { p0: 42 },
    };
    mountUrl('/charts?' + new URLSearchParams(serializeChartUrlState(state)));
    await waitFor(() => expect(searchSql).toHaveBeenCalledOnce());
    expect(screen.getByRole('tab', { name: '柱状图' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('mock-sql-editor')).toHaveValue(state.sql);
    expect(vi.mocked(searchSql).mock.calls[0][0]).toMatchObject({ sql: state.sql, parameters: state.parameters, ...state.range });
    expect(urlState().state).toMatchObject({ editor: { chartType: 'bar', mode: 'sql', source: 'metrics' }, sql: state.sql, parameters: state.parameters, range: state.range });
  });

  it.each(CHART_URL_TABS)('%s Builder 深链首查恢复条件且不会被隐藏页签覆盖', async (tab) => {
    const series = [{ id: 'stable', ref: 'A', aggFunc: 'count' as const, field: '', where: "service = 'api'", alias: 'Calls' }];
    const state: ChartUrlState = {
      editor: { version: 1, chartType: tab, mode: 'builder', source: tab === 'heatmap' ? 'traces' : 'logs',
        series, formulas: [], groupBy: tab === 'table' ? ['service'] : [], granularity: 'auto', syntax: 'sql',
        where: "service = 'api'", patternExpression: tab === 'patterns' ? 'body' : undefined,
        ...(tab === 'table' ? { tableSeries: series, tableQuery: { topN: 7, sort: [{ columnId: 'stable', direction: 'desc' as const }] } } : {}),
        ...(tab === 'search' ? { eventSearch: { domain: 'logs' as const, fields: 'ts, body', syntax: 'sql' as const, where: "service = 'api'", orderBy: 'ts DESC', filters: [] } } : {}),
      },
      range: { from: '2026-11-18T00:00:00.000Z', to: '2026-11-18T02:00:00.000Z' },
      ...(tab === 'heatmap' ? { heatmap: { field: 'duration_us' as const, bins: 20 } } : {}),
    };
    mountUrl('/charts?' + new URLSearchParams(serializeChartUrlState(state)));
    await waitFor(() => expect(searchSql).toHaveBeenCalled());
    const request = vi.mocked(searchSql).mock.calls[0][0];
    expect(request).toMatchObject(state.range);
    expect(Object.values(request.parameters ?? {})).toContain('api');
    expect(request.sql).not.toContain("'api'");
    expect(urlState().tab).toBe(tab);
    expect(urlState().state?.editor.source).toBe(state.editor.source);
    if (tab === 'table') expect(urlState().state?.editor.tableQuery).toEqual(state.editor.tableQuery);
    if (tab === 'heatmap') expect(urlState().state?.heatmap).toEqual(state.heatmap);
  });

  it('同页导航和浏览器返回恢复 URL 而不被旧 Tab 覆盖', async () => {
    mountUrl(); await ready();
    fireEvent.click(screen.getByRole('button', { name: '导航到模式链接' }));
    await waitFor(() => expect(screen.getByRole('tab', { name: '模式' })).toHaveAttribute('aria-selected', 'true'));
    await waitFor(() => expect(urlState().tab).toBe('patterns'));
    fireEvent.click(screen.getByRole('button', { name: '返回链接' }));
    await waitFor(() => expect(screen.getByRole('tab', { name: '时序图' })).toHaveAttribute('aria-selected', 'true'));
    await waitFor(() => expect(urlState().tab).toBe('time_series'));
  });

  it('样本失败不吞掉错误，也不隐藏成功图表', async () => {
    vi.mocked(searchSql).mockImplementation(async ({ sql }) => { if (sql.includes('ORDER BY ts DESC')) throw new Error('样本查询失败'); return chartResponse; });
    mount(); await ready();
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    expect(screen.getByRole('alert')).toHaveTextContent('样本查询失败');
    expect(screen.getByTestId('mock-chart')).toBeTruthy();
  });

});
