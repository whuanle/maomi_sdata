import { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ExecuteSearchSqlRequest } from '@/api/types';
import { searchSql } from '@/api/search';
import '@/i18n';
import { WhereSuggestInput } from '../WhereSuggestInput';

vi.mock('@/api/search', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/search')>()),
  searchSql: vi.fn(),
}));

/** severity 值分布响应（值建议查询返回）. */
const SEVERITY_VALUES = {
  columns: ['v', 'c'],
  rows: [['ERROR', 120], ['WARN', 45], ['INFO', 900]],
  elapsedMs: 1,
  truncated: false,
};

/** attributes["http"] 节点子属性响应（JSON 下钻建议）. */
const HTTP_KEYS = {
  columns: ['v', 'c'],
  rows: [['status_code', 77], ['method', 80]],
  elapsedMs: 1,
  truncated: false,
};

function Harness({ syntax = 'lucene', initial = '', onRun }: { syntax?: 'lucene' | 'sql'; initial?: string; onRun?: () => void }) {
  const [value, setValue] = useState(initial);
  return (
    <WhereSuggestInput
      domain="logs"
      syntax={syntax}
      value={value}
      onChange={setValue}
      onRun={onRun ?? (() => {})}
      testId="suggest-input"
      from="2026-11-17T00:00:00Z"
      to="2026-11-17T01:00:00Z"
      denoise={false}
    />
  );
}

/** 按 SQL 形状分发：severity 值分布 / attributes 子属性枚举 / 其余空响应. */
function mockSuggestResponses() {
  vi.mocked(searchSql).mockImplementation(async (request: ExecuteSearchSqlRequest) => {
    if (Object.values(request.parameters ?? {}).includes('$."http"')) return HTTP_KEYS;
    if (request.sql.includes('severity AS v')) return SEVERITY_VALUES;
    return { columns: ['v', 'c'], rows: [], elapsedMs: 1, truncated: false };
  });
}

describe('WhereSuggestInput', () => {
  beforeEach(() => {
    vi.mocked(searchSql).mockClear();
    mockSuggestResponses();
  });

  it('裸词输入建议物理列与 JSON 根，采纳字段补冒号（onChange 收到替换后文本）', async () => {
    render(<Harness />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'sev' } });
    // 建议列表出现 severity（hint 为字段类型）
    expect(await screen.findByRole('option', { name: /severity/ })).toBeTruthy();
    fireEvent.keyDown(input, { key: 'Enter' });
    // 采纳：词元替换为 severity:（值阶段衔接）
    await waitFor(() => {
      expect((screen.getByTestId('suggest-input') as HTMLInputElement).value).toBe('severity:');
    });
  });

  it('field: 之后建议该字段 top 值，采纳值带尾空格且不触发运行', async () => {
    const onRun = vi.fn();
    render(<Harness initial="severity:" onRun={onRun} />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'severity:ER' } });
    // 值建议来自服务端 facet（ERROR 在列）
    expect(await screen.findByRole('option', { name: /ERROR/ })).toBeTruthy();
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'ArrowUp' });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => {
      expect((screen.getByTestId('suggest-input') as HTMLInputElement).value).toBe('severity:ERROR ');
    });
    // 弹层打开时 Enter 被采纳截断，不触发运行
    expect(onRun).not.toHaveBeenCalled();
  });

  it('无弹层 Enter 直接触发运行；Esc 关闭建议', async () => {
    const onRun = vi.fn();
    render(<Harness initial="severity:ERROR " onRun={onRun} />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    // 词元为空：字段建议弹出，Esc 关闭后 Enter 直接运行
    fireEvent.keyDown(input, { key: 'Escape' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onRun).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('option')).toBeNull();
  });

  it('JSON 括号路径前缀建议下一层子属性（服务端 json_keys）', async () => {
    render(<Harness />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'attributes["http"]["' } });
    // 子属性候选来自绑定路径的 json_keys 查询
    expect(await screen.findByRole('option', { name: /status_code/ })).toBeTruthy();
    fireEvent.click(screen.getByRole('option', { name: /status_code/ }));
    await waitFor(() => {
      expect((screen.getByTestId('suggest-input') as HTMLInputElement).value).toBe('attributes["http"]["status_code"]');
    });
  });

  it('从根路径发现属性，采纳后可继续选择值或下钻', async () => {
    const onRun = vi.fn();
    vi.mocked(searchSql).mockImplementation(async ({ sql, parameters }) => {
      if (sql.includes('json_keys') && Object.values(parameters ?? {}).includes('$')) return { ...HTTP_KEYS, rows: [['http', 80], ['123', 1]] };
      if (sql.includes('json_keys')) return HTTP_KEYS;
      return { ...SEVERITY_VALUES, rows: [['POST', 80]] };
    });
    render(<Harness onRun={onRun} />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'attributes[' } });
    fireEvent.click(await screen.findByRole('option', { name: /http/ }));
    expect(input).toHaveValue('attributes["http"]');
    fireEvent.click(screen.getByRole('option', { name: /展开子属性/ }));
    expect(input).toHaveValue('attributes["http"][');
    fireEvent.click(await screen.findByRole('option', { name: /method/ }));
    fireEvent.click(screen.getByRole('option', { name: /筛选此属性的值/ }));
    expect(input).toHaveValue('attributes["http"]["method"]:');
    fireEvent.click(await screen.findByRole('option', { name: /POST/ }));
    expect(input).toHaveValue('attributes["http"]["method"]:POST ');
    expect(onRun).not.toHaveBeenCalled();
  });

  it('空子属性和查询失败不再静默消失且可重试', async () => {
    vi.mocked(searchSql).mockRejectedValue(new Error('offline'));
    render(<Harness />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'attributes[' } });
    expect(await screen.findByRole('alert')).toHaveTextContent('offline');
    vi.mocked(searchSql).mockResolvedValue({ ...HTTP_KEYS, rows: [] });
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    expect(await screen.findByText('无子属性（标量或空对象）')).toBeInTheDocument();
  });

  it('SQL 模式建议物理列与关键字（含 AND/LIKE）', async () => {
    render(<Harness syntax="sql" />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'serv' } });
    expect(await screen.findByRole('option', { name: /service/ })).toBeTruthy();
    fireEvent.change(input, { target: { value: 'service LIKE ' } });
    expect(await screen.findByRole('option', { name: 'LIKE' })).toBeTruthy();
  });

  it('IME 组合输入期间 Enter 不拦截（不采纳也不运行）', async () => {
    const onRun = vi.fn();
    render(<Harness initial="severity:ER" onRun={onRun} />);
    const input = screen.getByTestId('suggest-input');
    fireEvent.focus(input);
    await waitFor(() => {
      expect(screen.getAllByRole('option').length).toBeGreaterThan(0);
    });
    fireEvent.keyDown(input, { key: 'Enter', isComposing: true });
    expect(onRun).not.toHaveBeenCalled();
    // 值未变（未采纳候选）
    expect((input as HTMLInputElement).value).toBe('severity:ER');
  });
});
