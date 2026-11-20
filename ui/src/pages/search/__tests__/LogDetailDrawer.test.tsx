import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import '@/i18n';
import { LogDetailDrawer } from '../LogDetailDrawer';
import { searchSql } from '@/api/search';

// mock SQL 直查（工厂提升用字面量；按 SQL 形状在 beforeEach 分发）
vi.mock('@/api/search', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/search')>()),
  searchSql: vi.fn(),
}));

/** 结果表行（仅所选列，无 attributes/resource —— 由抽屉身份补查补全）. */
const tableRow = {
  ts: '2026-11-16T10:00:01.123Z',
  service: 'order-service',
  severity: 'INFO',
  body: '订单 S0203252952 状态变更',
  trace_id: 'f5f55ed7b7b5a479388de8521eb396a2',
};

/** 身份补查返回的完整记录（含未选列）. */
const fullRecord = {
  id: 12345,
  ts: tableRow.ts,
  service: 'order-service',
  severity: 'INFO',
  body: '订单 S0203252952 状态变更 WAIT_PAY → PAID（金额 ¥1320.53）',
  trace_id: tableRow.trace_id,
  span_id: '89a809d1b389bdad',
  source: 'application',
  attributes: '{"amount":"1320.53","order.no":"S0203252952"}',
  resource: '{"service.name":"order-service","service.version":"2.4.1","host.name":"gateway-01","deployment.environment":"dev","service.language":"go","telemetry.sdk":"sdata-demo-client"}',
};

function renderDrawer() {
  const onTraceActivate = vi.fn();
  const onFilterByValue = vi.fn();
  const onFilterOnlyValue = vi.fn();
  const onClose = vi.fn();
  render(
    <LogDetailDrawer
      domain="logs"
      row={tableRow}
      onClose={onClose}
      onFilterByValue={onFilterByValue}
      onFilterOnlyValue={onFilterOnlyValue}
      onTraceActivate={onTraceActivate}
    />,
  );
  return { onTraceActivate, onFilterByValue, onFilterOnlyValue, onClose };
}

describe('LogDetailDrawer', () => {
  beforeEach(() => {
    vi.mocked(searchSql).mockClear();
    // 按 SQL 形状分发：身份补查（ts = $ts）/ 链路日志（trace_id = $trace_id）/ 上下文（service = $service）
    vi.mocked(searchSql).mockImplementation(async (request) => {
      if (request.sql.includes('ts = $ts')) {
        return {
          columns: Object.keys(fullRecord),
          rows: [Object.values(fullRecord)],
          elapsedMs: 2,
          truncated: false,
        };
      }

      if (request.sql.includes('trace_id = $trace_id')) {
        return {
          columns: ['ts', 'service', 'severity', 'body', 'trace_id', 'span_id'],
          rows: [
            [tableRow.ts, 'order-service', 'INFO', '支付成功：流水号 PAY523049483', tableRow.trace_id, 'span-2'],
            [tableRow.ts, 'payment-service', 'ERROR', '风控决策 FAIL', tableRow.trace_id, 'span-3'],
          ],
          elapsedMs: 2,
          truncated: false,
        };
      }

      return {
        columns: ['ts', 'service', 'severity', 'body', 'trace_id'],
        rows: [[tableRow.ts, 'order-service', 'INFO', '上下文日志', tableRow.trace_id]],
        elapsedMs: 2,
        truncated: false,
      };
    });
  });

  it('概览 tab：身份补查完整记录并渲染消息体/属性/资源 chip', async () => {
    const { onTraceActivate } = renderDrawer();
    // 补查请求：logs 全列 + 身份条件（ts/service 等参数绑定，按天表范围覆盖目标行当天）
    await waitFor(() => {
      const identityCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('ts = $ts'));
      expect(identityCalls.length).toBe(1);
      expect(identityCalls[0][0].sql).toContain('FROM {{logs}}');
      expect(identityCalls[0][0].parameters).toMatchObject({ ts: tableRow.ts, service: 'order-service', trace_id: tableRow.trace_id });
    });
    // 概览分区：消息体为补查后的完整 body，attributes 键与 resource chip 渲染
    expect(await screen.findByText(/WAIT_PAY → PAID/)).toBeTruthy();
    expect(screen.getByText('顶层属性')).toBeTruthy();
    expect(screen.getByText('order.no')).toBeTruthy();
    expect(screen.getByText(/deployment.environment/)).toBeTruthy();
    // 页脚「查看链路」→ 深链跳转回调
    fireEvent.click(screen.getByRole('button', { name: '查看链路' }));
    expect(onTraceActivate).toHaveBeenCalledWith(tableRow.trace_id);
  });

  it('属性搜索框按键/值过滤键值对列表', async () => {
    renderDrawer();
    await screen.findByText('顶层属性');
    await screen.findByText('order.no');
    const searchBoxes = screen.getAllByPlaceholderText('搜索属性…');
    // 顶层属性搜索框：按不匹配的关键字过滤（仅顶层列表无匹配；日志属性列表未过滤仍正常展示）
    fireEvent.change(searchBoxes[0], { target: { value: 'order.no' } });
    expect(screen.getAllByText('无匹配属性').length).toBe(1);
    fireEvent.change(searchBoxes[0], { target: { value: '' } });
    // 日志属性搜索框：按键匹配保留 order.no、过滤掉 amount
    fireEvent.change(searchBoxes[1], { target: { value: 'order' } });
    expect(screen.getByText('order.no')).toBeTruthy();
    expect(screen.queryByText('amount')).toBeNull();
  });

  it('列值 tab：整条记录渲染为单棵 JSON 结构树（可折叠 + 分支过滤）', async () => {
    renderDrawer();
    await screen.findByText('顶层属性');
    // Radix Tabs 以 mousedown 激活
    fireEvent.mouseDown(screen.getByRole('tab', { name: '列值' }));
    // 全记录一棵树：标量列与 JSON 列同为树节点（attributes/resource 解析为嵌套结构，非逐字段键值网格；叶子字符串带引号渲染）
    expect(await screen.findByText('"application"')).toBeTruthy();
    expect(screen.getByText('{2 keys}')).toBeTruthy();
    expect(screen.getByText('{6 keys}')).toBeTruthy();
    expect(screen.getByText('order.no')).toBeTruthy();
    expect(screen.getByText('span_id')).toBeTruthy();
    // 树节点可收展（resource 收起后叶子隐藏）
    fireEvent.click(screen.getByRole('button', { name: 'collapse: resource' }));
    expect(screen.queryByText('deployment.environment')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'expand: resource' }));
    expect(screen.getByText('deployment.environment')).toBeTruthy();
    // 记录级搜索按分支过滤：命中 source 值的分支保留，无命中的 attributes/resource 分支整体剔除
    fireEvent.change(screen.getByPlaceholderText('搜索属性…'), { target: { value: 'application' } });
    expect(screen.getByText('source')).toBeTruthy();
    expect(screen.queryByText('{2 keys}')).toBeNull();
    expect(screen.queryByText('{6 keys}')).toBeNull();
    expect(screen.queryByText('order.no')).toBeNull();
  });

  it('链路日志 tab：按 trace_id 查询并渲染列表，点击条目切换抽屉目标', async () => {
    renderDrawer();
    await screen.findByText('顶层属性');
    fireEvent.mouseDown(screen.getByRole('tab', { name: '链路日志' }));
    await waitFor(() => {
      const traceCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('WHERE trace_id = $trace_id'));
      expect(traceCalls.length).toBe(1);
      expect(traceCalls[0][0].sql).toContain('FROM {{logs}}');
      expect(traceCalls[0][0].parameters).toMatchObject({ trace_id: tableRow.trace_id });
    });
    expect(await screen.findByText('支付成功：流水号 PAY523049483')).toBeTruthy();
    expect(screen.getByText('风控决策 FAIL')).toBeTruthy();
    // 点击链路日志条目 → 抽屉目标切换为该日志（重新身份补查，页签回概览）
    fireEvent.click(screen.getByText('风控决策 FAIL'));
    await waitFor(() => {
      expect(vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('ts = $ts')).length).toBe(2);
    });
    expect(screen.getByRole('tab', { name: '概览' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByText('消息体')).toBeTruthy();
  });

  it('上下文 tab：默认任意匹配（仅 ±30s 窗）查询邻域日志', async () => {
    renderDrawer();
    await screen.findByText('顶层属性');
    fireEvent.mouseDown(screen.getByRole('tab', { name: '上下文' }));
    await waitFor(() => {
      const contextCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('ts >= $start'));
      expect(contextCalls.length).toBe(1);
      expect(contextCalls[0][0].sql).toContain('FROM {{logs}}');
      expect(contextCalls[0][0].sql).not.toContain('service = $service');
    });
    expect(await screen.findByText('上下文日志')).toBeTruthy();
  });

  it('上下文 tab：切同服务/点选属性 chip 生成匹配条件（参数绑定）', async () => {
    renderDrawer();
    await screen.findByText('顶层属性');
    fireEvent.mouseDown(screen.getByRole('tab', { name: '上下文' }));
    await screen.findByText('上下文日志');
    // 切「同服务」→ service 列条件（上下文查询签名含 ts >= $start，与身份补查区分）
    fireEvent.click(screen.getByRole('button', { name: '同服务' }));
    await waitFor(() => {
      const serviceCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('service = $service') && request.sql.includes('ts >= $start'));
      expect(serviceCalls.length).toBe(1);
      expect(serviceCalls[0][0].parameters).toMatchObject({ service: 'order-service' });
    });
    // 点选属性 chip（amount = 1320.53）→ attributes 路径编译 + $m0 绑定
    fireEvent.click(screen.getByRole('button', { name: 'amount = 1320.53' }));
    await waitFor(() => {
      const attrCalls = vi.mocked(searchSql).mock.calls.filter(([request]) => request.sql.includes('json_valid(attributes'));
      const last = attrCalls[attrCalls.length - 1][0];
      expect(last.sql).toMatch(/json_extract_string\(CASE WHEN json_valid\(attributes\) THEN attributes ELSE NULL END, \$jp\d+\) = \$m0/);
      expect(last.parameters).toMatchObject({ m0: '1320.53' });
    });
    // 匹配中 chip 可移除
    expect(screen.getByText('匹配 1 个属性')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '移除匹配属性: amount' }));
    await waitFor(() => {
      expect(screen.getByText('匹配 0 个属性')).toBeTruthy();
    });
  });

  it('概览值行悬浮动作：按此值过滤 / 仅此值回调', async () => {
    const { onFilterByValue, onFilterOnlyValue } = renderDrawer();
    await screen.findByText('顶层属性');
    // 顶层属性 severity 行的悬浮动作（field 为物理列名）
    fireEvent.click(screen.getByRole('button', { name: '按此值过滤: severity' }));
    expect(onFilterByValue).toHaveBeenCalledWith('severity', 'INFO');
    fireEvent.click(screen.getByRole('button', { name: '仅此值: severity' }));
    expect(onFilterOnlyValue).toHaveBeenCalledWith('severity', 'INFO');
    // 日志属性行：field 为 attributes. 路径
    fireEvent.click(screen.getByRole('button', { name: '按此值过滤: attributes.order.no' }));
    expect(onFilterByValue).toHaveBeenCalledWith('attributes.order.no', 'S0203252952');
  });

  it('列值 tab：搜索关键字按键名命中分支（命中子键保留，无命中的容器剔除）', async () => {
    renderDrawer();
    await screen.findByText('顶层属性');
    fireEvent.mouseDown(screen.getByRole('tab', { name: '列值' }));
    expect(await screen.findByText('{6 keys}')).toBeTruthy();
    // 按键名 host.name 搜索：resource 容器仅保留命中子键
    fireEvent.change(screen.getByPlaceholderText('搜索属性…'), { target: { value: 'host.name' } });
    expect(screen.getByText('{1 keys}')).toBeTruthy();
    expect(screen.getByText('host.name')).toBeTruthy();
    expect(screen.queryByText('{2 keys}')).toBeNull();
    expect(screen.queryByText('order.no')).toBeNull();
  });
});
