import { createContext, useContext, useRef } from 'react';
import { Outlet } from 'react-router';
import type { DashboardConfig, DashboardItem } from '@/api/types';
import type { TimeRange } from '@/design-system';

export interface DashboardSnapshot { item: DashboardItem; config: DashboardConfig }
export interface DashboardSessionState {
  snapshot: DashboardSnapshot; draft: DashboardSnapshot; range: TimeRange; refreshSeconds: number; editing: boolean;
}
const Context = createContext<{ current: DashboardSessionState | null } | null>(null);
/** 父路由保留内存草稿，离开仪表盘即释放，不持久化查询与参数. */
export function DashboardSession() {
  const session = useRef<DashboardSessionState | null>(null);
  return <Context.Provider value={session}><Outlet /></Context.Provider>;
}
export const useDashboardSession = () => useContext(Context);
