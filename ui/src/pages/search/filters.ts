import type { FilterOp } from './lucene';

/**
 * 过滤器面板的单条过滤条件（值经参数绑定，不拼接 SQL）.
 * 标量操作符用 value；in/nin（过滤器树值勾选，组内 OR）用 values 列表；exists/nexists 两者皆空.
 */
export interface SearchFilter {
  id: number;
  field: string;
  op: FilterOp;
  value: string;
  /** in/nin 值列表 */
  values?: string[];
}

/** 某字段在过滤器集合中的勾选状态（过滤器树三态复选框数据源）. */
export interface FieldFilterState {
  /** 勾选（并入 in 过滤；eq 单值过滤亦视作勾选） */
  included: Set<string>;
  /** 排除（并入 nin 过滤；neq 单值过滤亦视作排除） */
  excluded: Set<string>;
  /** 存在键存在过滤 */
  exists: boolean;
  /** 存在键不存在过滤 */
  nexists: boolean;
}

/** 读取某字段的过滤状态（eq/neq 单值视作 include/exclude，树与 chips/明细入口互通）. */
export function readFieldState(filters: SearchFilter[], field: string): FieldFilterState {
  const state: FieldFilterState = { included: new Set(), excluded: new Set(), exists: false, nexists: false };
  for (const filter of filters) {
    if (filter.field !== field) {
      continue;
    }

    if (filter.op === 'eq') {
      state.included.add(filter.value);
    } else if (filter.op === 'neq') {
      state.excluded.add(filter.value);
    } else if (filter.op === 'in') {
      (filter.values ?? []).forEach((value) => state.included.add(value));
    } else if (filter.op === 'nin') {
      (filter.values ?? []).forEach((value) => state.excluded.add(value));
    } else if (filter.op === 'exists') {
      state.exists = true;
    } else if (filter.op === 'nexists') {
      state.nexists = true;
    }
  }

  return state;
}

/** 值勾选动作：include（勾选）/ exclude（排除）/ none（清除）. */
export type ValueToggle = 'include' | 'exclude' | 'none';

/** 移除某字段的全部值类过滤（in/nin/eq/neq，树切换值时整体重写）. */
function dropValueFilters(filters: SearchFilter[], field: string): SearchFilter[] {
  return filters.filter((filter) => !(filter.field === field && ['in', 'nin', 'eq', 'neq'].includes(filter.op)));
}

/**
 * 值勾选切换 → 重写该字段的 in/nin 过滤（含 converts eq/neq 单值为列表，保证树/chips/明细入口同步）.
 * 同一字段的 include 与 exclude 可并存（SQL 侧 IN ... AND NOT IN ...）.
 */
export function applyValueToggle(filters: SearchFilter[], field: string, value: string, next: ValueToggle): SearchFilter[] {
  const state = readFieldState(filters, field);
  const included = new Set(state.included);
  const excluded = new Set(state.excluded);
  included.delete(value);
  excluded.delete(value);
  if (next === 'include') {
    included.add(value);
  } else if (next === 'exclude') {
    excluded.add(value);
  }

  const rest = dropValueFilters(filters, field);
  const appended: SearchFilter[] = [];
  if (included.size > 0) {
    appended.push({ id: Date.now(), field, op: 'in', value: '', values: [...included] });
  }

  if (excluded.size > 0) {
    appended.push({ id: Date.now() + appended.length, field, op: 'nin', value: '', values: [...excluded] });
  }

  return [...rest, ...appended];
}

/** 「仅此值」（HyperDX Only）：清空该字段值过滤，仅保留此值的 in 过滤. */
export function applyValueOnly(filters: SearchFilter[], field: string, value: string): SearchFilter[] {
  const rest = dropValueFilters(filters, field);
  return [...rest, { id: Date.now(), field, op: 'in', value: '', values: [value] }];
}

/**
 * key 行存在性切换 → 重写该字段的 exists/nexists 过滤（二者互斥，再次点击取消）.
 * @param next exists（包含存在该 key 的行）/ nexists（排除）/ none（取消）
 */
export function applyKeyToggle(filters: SearchFilter[], field: string, next: 'exists' | 'nexists' | 'none'): SearchFilter[] {
  const rest = filters.filter((filter) => !(filter.field === field && (filter.op === 'exists' || filter.op === 'nexists')));
  if (next === 'none') {
    return rest;
  }

  return [...rest, { id: Date.now(), field, op: next, value: '' }];
}

/** 过滤 chip 文案（in/nin/exists/nexists 专用格式，其余 field op value）. */
export function formatFilterChip(filter: SearchFilter): string {
  if (filter.op === 'in' || filter.op === 'nin') {
    return `${filter.field} ${filter.op} (${(filter.values ?? []).join(', ')})`;
  }

  if (filter.op === 'exists' || filter.op === 'nexists') {
    return `${filter.field} ${filter.op}`;
  }

  return `${filter.field} ${filter.op} ${filter.value}`;
}
