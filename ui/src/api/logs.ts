import { get } from './client';
import type { LogItem, PagedQuery, PagedResult } from './types';

/** 日志查询参数. */
export interface LogQueryParams extends PagedQuery {
  start?: string;
  end?: string;
  service?: string;
  severity?: string;
  search?: string;
}

/** 日志查询. */
export function queryLogs(params: LogQueryParams) {
  return get<PagedResult<LogItem>>('/logs', params);
}

/** 日志服务名列表. */
export function listLogServices() {
  return get<string[]>('/logs/services');
}
