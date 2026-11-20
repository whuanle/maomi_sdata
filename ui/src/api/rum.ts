import { get, post } from './client';
import type { RumErrorAggregate, RumSessionSummary, RumReplayResponse, RumHeatmapCell, RumClickTarget, RumPageNode, RumProjectItem, SaveRumProjectCommand } from './types';

/** RUM 项目列表（T611；client 自动加 /api 前缀）. */
export function fetchRumProjects() {
  return get<{ page: number; pageSize: number; count: number; data: RumProjectItem[] }>('/rum-projects', { page: 1, pageSize: 100 });
}

export interface SessionQuery {
  projectKey: string;
  from: string;
  to: string;
  search?: string;
  hasErrors?: boolean;
  hasReplay?: boolean;
  page?: number;
  pageSize?: number;
  sort?: string;
}

export interface SessionEvent {
  timestamp: string;
  type: string;
  name?: string;
  pagePath?: string;
  value?: number;
  payload?: string;
}

export function searchRumSessions(params: SessionQuery) {
  return get<{ count: number; items: RumSessionSummary[] }>('/rum/sessions/search', params);
}

export function fetchSessionEvents(params: { projectKey: string; sessionId: string; from: string; to: string }) {
  return get<SessionEvent[]>('/rum/sessions/events', params);
}

export function saveRumProject(body: SaveRumProjectCommand) {
  return post<{ value: number }>('/rum-projects', body);
}

/** RUM 性能汇总（PV/UV + Web Vitals p75）. */
export function fetchRumPerformance(params: { projectKey: string; from: string; to: string }) {
  return get<{ pageViews: number; uniqueVisitors: number; vitalP75: Record<string, number> }>('/rum/performance', params);
}

/** RUM JS 错误 Top. */
export function fetchRumErrors(params: { projectKey: string; from: string; to: string; topN?: number }) {
  return get<{ items: RumErrorAggregate[] }>('/rum/errors', params);
}

/** RUM 会话列表. */
export function fetchRumSessions(params: { projectKey: string; from: string; to: string; limit?: number }) {
  return get<{ items: RumSessionSummary[] }>('/rum/sessions', params);
}

/** RUM 会话回放分段（gzip(rrweb 事件 JSON) 的 base64，按 seq 升序）. */
export function fetchRumReplay(sessionId: string, params: { projectKey: string; from: string; to: string }) {
  return get<RumReplayResponse>(`/rum/v1/replay/${encodeURIComponent(sessionId)}`, params);
}

/** RUM 点击热力（栅格 + 总数 + 目标 TopN，T615/T616）. */
export function fetchRumHeatmap(params: { projectKey: string; from: string; to: string; gridCells?: number; pagePath?: string }) {
  return get<{ cells: RumHeatmapCell[]; totalClicks: number; topTargets: RumClickTarget[] }>('/rum/heatmap', params);
}

/** RUM 站点地图页面树（T615）. */
export function fetchRumSitemap(params: { projectKey: string; from: string; to: string; limit?: number }) {
  return get<{ items: RumPageNode[] }>('/rum/sitemap', params);
}
