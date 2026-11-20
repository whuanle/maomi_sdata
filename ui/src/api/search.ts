import { post } from './client';
import type { AnalyticsQueryResponse, ExecuteSearchSqlRequest, SearchDomain } from './types';
import type { ChartSource } from './chartTypes';

/**
 * 统一搜索 SQL 直查（POST /analytics/search）.
 * SQL 中的 {{logs}}/{{traces}} 表占位符由服务端展开为时间区间内现存按天表 UNION ALL，
 * $start/$end 由服务端以参数绑定（值来自请求的时间范围，非 SQL 文本）.
 */
export function searchSql(request: ExecuteSearchSqlRequest) {
  return post<AnalyticsQueryResponse>('/analytics/search', request);
}

/** 各数据域表结构（DuckDB 按天分表物理列，与 TimeTables.CreateSql 一致）. */
export const SEARCH_FIELDS: Record<ChartSource, { field: string; kind: 'string' | 'number' | 'time' | 'json' }[]> = {
  iot: [{ field: 'ts', kind: 'time' }, ...['device_key', 'signal', 'str_value'].map((field) => ({ field, kind: 'string' as const })), ...['num_value', 'quality'].map((field) => ({ field, kind: 'number' as const }))],
  metrics: [{ field: 'ts', kind: 'time' }, { field: 'name', kind: 'string' }, { field: 'value', kind: 'number' }, { field: 'labels', kind: 'json' }, { field: 'kind', kind: 'string' }],
  sessions: [{ field: 'ts', kind: 'time' }, ...['project_key', 'session_id', 'user_agent', 'domain'].map((field) => ({ field, kind: 'string' as const }))],
  logs: [
    { field: 'ts', kind: 'time' },
    { field: 'service', kind: 'string' },
    { field: 'severity', kind: 'string' },
    { field: 'body', kind: 'string' },
    { field: 'trace_id', kind: 'string' },
    { field: 'span_id', kind: 'string' },
    { field: 'source', kind: 'string' },
    { field: 'attributes', kind: 'json' },
    { field: 'resource', kind: 'json' },
  ],
  traces: [
    { field: 'ts', kind: 'time' },
    { field: 'service', kind: 'string' },
    { field: 'name', kind: 'string' },
    { field: 'duration_us', kind: 'number' },
    { field: 'kind', kind: 'string' },
    { field: 'status', kind: 'string' },
    { field: 'trace_id', kind: 'string' },
    { field: 'span_id', kind: 'string' },
    { field: 'parent_span_id', kind: 'string' },
    { field: 'attributes', kind: 'json' },
    { field: 'resource', kind: 'json' },
    { field: 'events', kind: 'json' },
  ],
};

/** 各数据域直方图默认堆叠维度（结果集中存在该列时启用堆叠）. */
export const HISTOGRAM_GROUP_FIELD: Record<SearchDomain, string> = {
  logs: 'severity',
  traces: 'status',
};
