import dayjs from 'dayjs';
import type { SearchDomain } from '@/api/types';
import { TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';
import type { FilterOp } from './lucene';
import type { SearchFilter } from './filters';

/** 行数上限选项（服务端硬上限 10000）. */
export const LIMIT_OPTIONS = [100, 200, 500, 1000, 5000];

/** 定时刷新间隔选项（秒，HyperDX 式 1s/2s/4s/10s/30s；0 = 关闭）. */
export const REFRESH_INTERVAL_OPTIONS = [1, 2, 4, 10, 30];

/** 各数据域默认显示字段与排序（行1 SELECT/ORDER BY 关键字条初始值，两种语法同构）. */
export const DEFAULT_FIELDS: Record<SearchDomain, string> = {
  logs: 'ts, service, severity, body, trace_id',
  traces: 'ts, service, name, duration_us, status, trace_id',
};
export const DEFAULT_ORDER_BY: Record<SearchDomain, string> = {
  logs: 'ts DESC',
  traces: 'ts DESC',
};

/** 整句 SQL → WHERE 片段（旧版整句快照/旧地址回退）：截取 WHERE 至 ORDER BY/GROUP BY/LIMIT，剥离时间窗样板. */
export function extractWhereFragment(sqlText: string): string {
  const match = sqlText.match(/\bWHERE\b([\s\S]*?)(?:\bORDER\s+BY\b|\bGROUP\s+BY\b|\bLIMIT\b|$)/i);
  if (!match) {
    return '';
  }

  return match[1]
    .replace(/^\s*ts\s*>=\s*\$start(\s+AND\s+ts\s*<\s*\$end)?/i, '')
    .replace(/^\s*AND\s+/i, '')
    .trim();
}

/** 缺省条件（与地址栏省略该参数等价）. */
export const DEFAULT_LIMIT = 500;
export const DEFAULT_REFRESH_INTERVAL_SEC = 10;
export const DEFAULT_PRESET_KEY = '15m';

/** URL 可恢复的搜索条件（查询条件 + 时间窗；视图偏好与瀑布图弹层不入 URL，链路深链走既有 traceId 参数）. */
export interface SearchUrlState {
  source: SearchDomain;
  syntax: 'lucene' | 'sql';
  /** Lucene 属性搜索表达式（mode=lucene 的 q 参数）. */
  whereText: string;
  /** SQL 模式手写 WHERE 片段（mode=sql 的 q 参数；与 Lucene 同参不同语义）. */
  sqlWhere: string;
  fieldsText: string;
  orderByText: string;
  filters: SearchFilter[];
  denoise: boolean;
  timeRange: TimeRange;
  limit: number;
  refreshIntervalSec: number;
}

/** 无参数时的默认条件（15m 预设滑窗，绝对值为占位、执行时按预设实时计算）. */
export function defaultSearchUrlState(source: SearchDomain): SearchUrlState {
  return {
    source,
    syntax: 'lucene',
    whereText: '',
    sqlWhere: '',
    fieldsText: DEFAULT_FIELDS[source],
    orderByText: DEFAULT_ORDER_BY[source],
    filters: [],
    denoise: false,
    timeRange: presetTimeRange(DEFAULT_PRESET_KEY),
    limit: DEFAULT_LIMIT,
    refreshIntervalSec: DEFAULT_REFRESH_INTERVAL_SEC,
  };
}

/** 预设 key → 相对滑窗 TimeRange（终点 now；执行时以预设实时计算，绝对值仅作展示占位）. */
function presetTimeRange(key: string): TimeRange {
  const ms = TIME_RANGE_PRESETS.find((preset) => preset.key === key)?.ms ?? 15 * 60_000;
  return { from: dayjs().subtract(ms).toISOString(), to: dayjs().toISOString(), preset: key };
}

/** URL 参数集 → 条件状态（非法值安全回落默认，保证任意地址可打开不报错）. */
export function parseSearchUrlState(params: URLSearchParams): SearchUrlState {
  // source 与既有约定一致：非 'traces' 一律回落 logs（/logs /traces 重定向依赖）
  const source: SearchDomain = params.get('source') === 'traces' ? 'traces' : 'logs';
  const state = defaultSearchUrlState(source);

  if (params.get('mode') === 'sql') {
    state.syntax = 'sql';
    const q = params.get('q');
    if (q !== null && q.trim().length > 0) {
      state.sqlWhere = q;
    } else {
      // 旧地址 mode=sql&sql=<整句>：回退提取 WHERE 主体（时间窗样板剥离）
      const legacy = params.get('sql');
      if (legacy !== null && legacy.trim().length > 0) {
        state.sqlWhere = extractWhereFragment(legacy);
      }
    }
  } else {
    const q = params.get('q');
    if (q !== null) {
      state.whereText = q;
    }
  }

  const fields = params.get('fields');
  if (fields) {
    state.fieldsText = fields;
  }

  const orderBy = params.get('orderBy');
  if (orderBy) {
    state.orderByText = orderBy;
  }

  const filtersRaw = params.get('filters');
  if (filtersRaw) {
    // 解析失败或条目形状非法：整体忽略（回落空过滤器），不部分恢复避免条件半还原
    const filters = decodeFilters(filtersRaw);
    if (filters !== null) {
      state.filters = filters;
    }
  }

  if (params.get('denoise') === '1') {
    state.denoise = true;
  }

  state.timeRange = parseTimeRange(params);

  // 参数缺失（null）不得进入 Number() 解析——Number(null) === 0 会误命中 refresh=0（关闭轮询）
  const limitRaw = params.get('limit');
  if (limitRaw !== null) {
    const limit = Number(limitRaw);
    if (LIMIT_OPTIONS.includes(limit)) {
      state.limit = limit;
    }
  }

  const refreshRaw = params.get('refresh');
  if (refreshRaw !== null) {
    const refresh = Number(refreshRaw);
    if (REFRESH_INTERVAL_OPTIONS.includes(refresh) || refresh === 0) {
      state.refreshIntervalSec = refresh;
    }
  }

  return state;
}

/** 预设 range key 优先；非法/缺失预设回落 from/to（须为合法且 to > from 的日期）；再回落默认 15m. */
function parseTimeRange(params: URLSearchParams): TimeRange {
  const rangeKey = params.get('range');
  if (rangeKey && TIME_RANGE_PRESETS.some((preset) => preset.key === rangeKey)) {
    return presetTimeRange(rangeKey);
  }

  const fromRaw = params.get('from');
  const toRaw = params.get('to');
  if (fromRaw && toRaw) {
    const from = dayjs(fromRaw);
    const to = dayjs(toRaw);
    if (from.isValid() && to.isValid() && to.isAfter(from)) {
      return { from: from.toISOString(), to: to.toISOString() };
    }
  }

  return presetTimeRange(DEFAULT_PRESET_KEY);
}

/** 条件状态 → URL 参数集（source 总写入兼容 /logs /traces 重定向；其余非默认省略保持地址简洁）. */
export function serializeSearchUrlState(state: SearchUrlState): Record<string, string> {
  const params: Record<string, string> = { source: state.source };

  if (state.syntax === 'sql') {
    params.mode = 'sql';
    // WHERE 片段与 Lucene 表达式共用 q 参数（mode 区分语义），空串省略保持地址简洁
    if (state.sqlWhere.trim().length > 0) {
      params.q = state.sqlWhere;
    }
  } else if (state.whereText.length > 0) {
    params.q = state.whereText;
  }

  if (state.fieldsText !== DEFAULT_FIELDS[state.source]) {
    params.fields = state.fieldsText;
  }

  if (state.orderByText !== DEFAULT_ORDER_BY[state.source]) {
    params.orderBy = state.orderByText;
  }

  if (state.filters.length > 0) {
    params.filters = JSON.stringify(encodeFilters(state.filters));
  }

  // 降噪仅对日志域生效（buildLuceneQuery 同规则）
  if (state.denoise && state.source === 'logs') {
    params.denoise = '1';
  }

  if (state.timeRange.preset) {
    if (state.timeRange.preset !== DEFAULT_PRESET_KEY) {
      params.range = state.timeRange.preset;
    }
  } else {
    params.from = state.timeRange.from;
    params.to = state.timeRange.to;
  }

  if (state.limit !== DEFAULT_LIMIT) {
    params.limit = String(state.limit);
  }

  if (state.refreshIntervalSec !== DEFAULT_REFRESH_INTERVAL_SEC) {
    params.refresh = String(state.refreshIntervalSec);
  }

  return params;
}

/** URL 中的过滤器条目（相对 SearchFilter 去掉临时 id）. */
interface UrlFilter {
  field: string;
  op: FilterOp;
  value?: string;
  values?: string[];
}

/** 条件状态过滤器 → URL 条目（去 id；标量操作符保留 value，in/nin 保留 values，存在性两者皆空）. */
function encodeFilters(filters: SearchFilter[]): UrlFilter[] {
  return filters.map((filter) => {
    const encoded: UrlFilter = { field: filter.field, op: filter.op };
    if (filter.values !== undefined) {
      encoded.values = filter.values;
    }

    if (filter.value.length > 0) {
      encoded.value = filter.value;
    }

    return encoded;
  });
}

/** URL 条目数组 → 条件状态过滤器；任一条目形状非法整体拒绝（返回 null）. */
function decodeFilters(raw: string): SearchFilter[] | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (!Array.isArray(parsed)) {
    return null;
  }

  const filters: SearchFilter[] = [];
  for (const entry of parsed) {
    const filter = decodeFilter(entry);
    if (filter === null) {
      return null;
    }

    filters.push(filter);
  }

  return filters;
}

/** 单条 URL 条目校验与还原（负数 id 与 Date.now() 生成 id 永不冲突）. */
function decodeFilter(entry: unknown): SearchFilter | null {
  if (typeof entry !== 'object' || entry === null) {
    return null;
  }

  const record = entry as Record<string, unknown>;
  const field = record.field;
  const op = record.op;
  if (typeof field !== 'string' || field.length === 0) {
    return null;
  }

  if (typeof op !== 'string' || !FILTER_OPS.includes(op as FilterOp)) {
    return null;
  }

  if (record.values !== undefined && !isStringArray(record.values)) {
    return null;
  }

  if (record.value !== undefined && typeof record.value !== 'string') {
    return null;
  }

  return {
    id: -1 - decodeSeq++,
    field,
    op: op as FilterOp,
    value: typeof record.value === 'string' ? record.value : '',
    values: isStringArray(record.values) ? record.values : undefined,
  };
}

const FILTER_OPS: FilterOp[] = ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'prefix', 'contains', 'in', 'nin', 'exists', 'nexists', 'root'];

/** 模块内自增序号：保证同批解码的过滤器 id 唯一. */
let decodeSeq = 0;

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}
