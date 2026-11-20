import { del, get, post } from './client';
import type { DashboardItem, DashboardTimeConfig, PanelConfig, PanelData, PagedQuery, PagedResult } from './types';

/** 仪表盘列表. */
export function queryDashboards(params?: PagedQuery & { kind?: 'view' }) {
  return get<PagedResult<DashboardItem>>('/dashboards', params);
}

/** 保存仪表盘. */
export function saveDashboard(dashboard: Partial<DashboardItem> & { expectedConfigJson?: string }) {
  return post<{ value: number }>('/dashboards', dashboard);
}

/** 仪表盘详情. */
export function getDashboard(id: number) {
  return get<DashboardItem>(`/dashboards/${id}`);
}

/** 删除仪表盘. */
export function deleteDashboard(id: number) {
  return del<unknown>(`/dashboards/${id}`);
}

/** 面板数据（可覆盖仪表盘时间范围）. */
export function getDashboardData(id: number, params?: { rowLimit?: number; from?: string; to?: string }) {
  return get<{ panels: PanelData[] }>(`/dashboards/${id}/data`, params);
}

/** 面板查询预览（编辑器实时预览，无需先保存）. */
export function previewPanel(panel: PanelConfig, from?: string, to?: string, rowLimit = 500, signal?: AbortSignal) {
  return post<PanelData>('/dashboards/preview-panel', { panel, from, to, rowLimit }, signal);
}

/** 仪表盘时间配置 → 绝对区间（ISO 字符串；preset=custom 用显式 from/to）. */
export function resolveDashboardRange(time?: DashboardTimeConfig | null): { from: string; to: string } {
  const now = Date.now();
  if (time?.preset === 'custom' && time.from && time.to) {
    return { from: time.from, to: time.to };
  }

  const presetMs: Record<string, number> = {
    '1m': 60_000,
    '5m': 5 * 60_000,
    '15m': 15 * 60_000,
    '30m': 30 * 60_000,
    '45m': 45 * 60_000,
    '1h': 60 * 60_000,
    '3h': 3 * 60 * 60_000,
    '6h': 6 * 60 * 60_000,
    '12h': 12 * 60 * 60_000,
    '1d': 24 * 60 * 60_000,
    '2d': 2 * 24 * 60 * 60_000,
    '7d': 7 * 24 * 60 * 60_000,
  };
  const span = presetMs[time?.preset ?? '1h'] ?? 60 * 60_000;
  return { from: new Date(now - span).toISOString(), to: new Date(now).toISOString() };
}
