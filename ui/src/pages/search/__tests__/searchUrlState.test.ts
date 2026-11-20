import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FIELDS,
  DEFAULT_ORDER_BY,
  defaultSearchUrlState,
  extractWhereFragment,
  parseSearchUrlState,
  serializeSearchUrlState,
} from '../searchUrlState';

/** 构造 URLSearchParams 的辅助（模拟地址栏 query）. */
function paramsOf(query: string): URLSearchParams {
  return new URLSearchParams(query);
}

describe('parseSearchUrlState', () => {
  it('无参数返回 logs 域默认条件', () => {
    const state = parseSearchUrlState(paramsOf(''));
    expect(state.source).toBe('logs');
    expect(state.syntax).toBe('lucene');
    expect(state.whereText).toBe('');
    expect(state.sqlWhere).toBe('');
    expect(state.fieldsText).toBe(DEFAULT_FIELDS.logs);
    expect(state.orderByText).toBe(DEFAULT_ORDER_BY.logs);
    expect(state.filters).toEqual([]);
    expect(state.denoise).toBe(false);
    expect(state.timeRange.preset).toBe('15m');
    expect(state.limit).toBe(500);
    expect(state.refreshIntervalSec).toBe(10);
  });

  it('恢复 Lucene 查询条件（q/fields/orderBy/limit/range/denoise）', () => {
    const state = parseSearchUrlState(paramsOf(
      'source=logs&q=severity%3AERROR%20timeout&fields=ts%2C%20body&orderBy=ts%20ASC&limit=1000&range=1h&denoise=1',
    ));
    expect(state.whereText).toBe('severity:ERROR timeout');
    expect(state.fieldsText).toBe('ts, body');
    expect(state.orderByText).toBe('ts ASC');
    expect(state.limit).toBe(1000);
    expect(state.timeRange.preset).toBe('1h');
    expect(state.denoise).toBe(true);
  });

  it('恢复 SQL 模式 WHERE 片段与 filters JSON（还原为带新 id 的过滤器）', () => {
    const filters = JSON.stringify([
      { field: 'service', op: 'eq', value: 'api' },
      { field: 'severity', op: 'in', values: ['ERROR', 'WARN'] },
      { field: 'k8s.pod', op: 'exists' },
    ]);
    const state = parseSearchUrlState(paramsOf(`mode=sql&q=${encodeURIComponent("severity = 'ERROR'")}&filters=${encodeURIComponent(filters)}`));
    expect(state.syntax).toBe('sql');
    expect(state.sqlWhere).toBe("severity = 'ERROR'");
    expect(state.filters).toHaveLength(3);
    expect(state.filters[0]).toMatchObject({ field: 'service', op: 'eq', value: 'api' });
    expect(state.filters[1]).toMatchObject({ field: 'severity', op: 'in', values: ['ERROR', 'WARN'] });
    expect(state.filters[2]).toMatchObject({ field: 'k8s.pod', op: 'exists', value: '' });
    // id 重新分配（负数，与运行时 Date.now() 生成的 id 永不冲突）
    expect(new Set(state.filters.map((filter) => filter.id)).size).toBe(3);
  });

  it('SQL 模式旧地址（整句 sql 参数）回退提取 WHERE 主体（时间窗样板剥离）', () => {
    const legacy = "SELECT ts FROM {{logs}} WHERE ts >= $start AND ts < $end AND severity = 'ERROR' ORDER BY ts DESC LIMIT 200";
    const state = parseSearchUrlState(paramsOf(`mode=sql&sql=${encodeURIComponent(legacy)}`));
    expect(state.syntax).toBe('sql');
    expect(state.sqlWhere).toBe("severity = 'ERROR'");
    // 无 WHERE 子句的整句回退为空片段
    expect(parseSearchUrlState(paramsOf(`mode=sql&sql=${encodeURIComponent('SELECT ts FROM {{logs}}')}`)).sqlWhere).toBe('');
  });

  it('自定义时间窗恢复 from/to（无 preset）', () => {
    const state = parseSearchUrlState(paramsOf('from=2026-11-17T00:00:00.000Z&to=2026-11-17T01:00:00.000Z'));
    expect(state.timeRange.preset).toBeUndefined();
    expect(state.timeRange.from).toBe('2026-11-17T00:00:00.000Z');
    expect(state.timeRange.to).toBe('2026-11-17T01:00:00.000Z');
  });

  it('非法值安全回落：未知预设/坏日期/越界 limit/未知 refresh', () => {
    const state = parseSearchUrlState(paramsOf('range=99h&limit=777&refresh=42&from=not-a-date&to=also-bad'));
    expect(state.timeRange.preset).toBe('15m');
    expect(state.limit).toBe(500);
    expect(state.refreshIntervalSec).toBe(10);
  });

  it('to 早于 from 的自定义区间回落默认预设', () => {
    const state = parseSearchUrlState(paramsOf('from=2026-11-17T02:00:00.000Z&to=2026-11-17T01:00:00.000Z'));
    expect(state.timeRange.preset).toBe('15m');
  });

  it('filters JSON 损坏或条目非法时整体忽略', () => {
    expect(parseSearchUrlState(paramsOf(`filters=${encodeURIComponent('{broken')}`))).toMatchObject({ filters: [] });
    expect(parseSearchUrlState(paramsOf(`filters=${encodeURIComponent('[{"field":"service"}]')}`))).toMatchObject({ filters: [] });
    expect(parseSearchUrlState(paramsOf(`filters=${encodeURIComponent('[{"field":"service","op":"hax","value":"x"}]')}`))).toMatchObject({ filters: [] });
  });

  it('source 非 traces 一律回落 logs（与既有 source 约定一致）', () => {
    expect(parseSearchUrlState(paramsOf('source=traces')).source).toBe('traces');
    expect(parseSearchUrlState(paramsOf('source=metrics')).source).toBe('logs');
  });

  it('mode=sql 无 q/legacy sql 时 sqlWhere 为空片段', () => {
    expect(parseSearchUrlState(paramsOf('mode=sql')).sqlWhere).toBe('');
    expect(parseSearchUrlState(paramsOf('mode=sql&sql=')).sqlWhere).toBe('');
  });
});

describe('serializeSearchUrlState', () => {
  it('默认条件仅写入 source（非默认省略）', () => {
    const params = serializeSearchUrlState(defaultSearchUrlState('logs'));
    expect(params).toEqual({ source: 'logs' });
  });

  it('非默认条件全部编码（q/fields/orderBy/limit/range/denoise）', () => {
    const state = {
      ...defaultSearchUrlState('logs'),
      whereText: 'severity:ERROR',
      fieldsText: 'ts, body',
      orderByText: 'ts ASC',
      limit: 1000,
      denoise: true,
      timeRange: { from: '', to: '', preset: '1h' },
    };
    expect(serializeSearchUrlState(state)).toEqual({
      source: 'logs',
      q: 'severity:ERROR',
      fields: 'ts, body',
      orderBy: 'ts ASC',
      limit: '1000',
      range: '1h',
      denoise: '1',
    });
  });

  it('SQL 模式写 mode + q（WHERE 片段与 Lucene 共用 q 参数，空片段省略）', () => {
    expect(serializeSearchUrlState({ ...defaultSearchUrlState('logs'), syntax: 'sql' })).toEqual({ source: 'logs', mode: 'sql' });
    const params = serializeSearchUrlState({ ...defaultSearchUrlState('logs'), syntax: 'sql', sqlWhere: "severity = 'ERROR'" });
    expect(params).toEqual({ source: 'logs', mode: 'sql', q: "severity = 'ERROR'" });
    // extractWhereFragment 直查：无 WHERE 子句回退空串
    expect(extractWhereFragment('SELECT 1')).toBe('');
  });

  it('自定义时间窗写 from/to，降噪仅日志域写入', () => {
    const params = serializeSearchUrlState({
      ...defaultSearchUrlState('logs'),
      denoise: true,
      timeRange: { from: '2026-11-17T00:00:00.000Z', to: '2026-11-17T01:00:00.000Z' },
    });
    expect(params.from).toBe('2026-11-17T00:00:00.000Z');
    expect(params.to).toBe('2026-11-17T01:00:00.000Z');
    expect(params.range).toBeUndefined();
    expect(serializeSearchUrlState({ ...defaultSearchUrlState('traces'), denoise: true }).denoise).toBeUndefined();
  });

  it('过滤器序列化去掉临时 id（in/nin 保留 values，存在性无值）', () => {
    const params = serializeSearchUrlState({
      ...defaultSearchUrlState('logs'),
      filters: [
        { id: 1, field: 'service', op: 'eq', value: 'api' },
        { id: 2, field: 'severity', op: 'nin', value: '', values: ['DEBUG'] },
        { id: 3, field: 'k8s.pod', op: 'nexists', value: '' },
      ],
    });
    expect(JSON.parse(params.filters)).toEqual([
      { field: 'service', op: 'eq', value: 'api' },
      { field: 'severity', op: 'nin', values: ['DEBUG'] },
      { field: 'k8s.pod', op: 'nexists' },
    ]);
  });

  it('refresh=0（关闭轮询）与非常规值均写入', () => {
    expect(serializeSearchUrlState({ ...defaultSearchUrlState('logs'), refreshIntervalSec: 0 }).refresh).toBe('0');
  });
});

describe('parse → serialize 往返一致性', () => {
  it('带全部非默认条件的地址往返后参数集不变（source 恒写入；比较参数值忽略编码与顺序差异）', () => {
    const query = 'source=traces&q=name%3A%22checkout%22&fields=ts%2C%20name&orderBy=duration_us%20DESC&limit=200&range=6h&refresh=30';
    const state = parseSearchUrlState(paramsOf(query));
    expect(Object.fromEntries(new URLSearchParams(serializeSearchUrlState(state)))).toEqual(Object.fromEntries(paramsOf(query)));
  });

  it('SQL 模式地址往返后参数集不变（mode + q 共用参数）', () => {
    const query = "source=logs&mode=sql&q=severity%20%3D%20%27ERROR%27&fields=ts%2C%20body&orderBy=ts%20ASC&limit=200&range=6h&refresh=30";
    const state = parseSearchUrlState(paramsOf(query));
    expect(Object.fromEntries(new URLSearchParams(serializeSearchUrlState(state)))).toEqual(Object.fromEntries(paramsOf(query)));
  });
});
