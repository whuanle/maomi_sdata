import { del, get, post } from './client';
import type { AnalyticsTableItem, AnalyticsQueryResponse, PagedQuery, PagedResult, SavedQueryItem } from './types';

/** 当前 DuckDB 表与字段目录. */
export function listAnalyticsTables() {
  return get<AnalyticsTableItem[]>('/analytics/tables');
}

/** 执行只读查询. */
export function executeQuery(sql: string, rowLimit = 1000) {
  return post<AnalyticsQueryResponse>('/analytics/query', { sql, rowLimit });
}

/** 保存的查询列表（kind 可选过滤：sql/search）. */
export function listSavedQueries(params?: PagedQuery & { kind?: string }) {
  return get<PagedResult<SavedQueryItem>>('/analytics/queries', params);
}

/** 按 id 查询保存项. */
export function getSavedQuery(id: number | string) {
  return get<SavedQueryItem>(`/analytics/queries/${id}`);
}

/** 保存查询. */
export function saveQuery(query: Omit<Partial<SavedQueryItem>, 'id'> & { id?: number | string }) {
  return post<{ value: number }>('/analytics/queries', query);
}

/** 删除保存的查询. */
export function deleteSavedQuery(id: number | string) {
  return del<unknown>(`/analytics/queries/${id}`);
}

/** 执行保存的查询. */
export function executeSavedQuery(id: number, rowLimit = 1000) {
  return post<AnalyticsQueryResponse>(`/analytics/queries/${id}/execute?rowLimit=${rowLimit}`);
}
