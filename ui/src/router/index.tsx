import { createBrowserRouter, Navigate, useLocation } from 'react-router';
import { AppLayout } from '@/layouts/AppLayout';
import { LoginPage } from '@/pages/login/LoginPage';
import { OverviewPage } from '@/pages/overview/OverviewPage';
import { SearchPage } from '@/pages/search/SearchPage';
import { SavedSearchPage } from '@/pages/saved-searches/SavedSearchPage';
import { SavedSearchesPage } from '@/pages/saved-searches/SavedSearchesPage';
import { ChartExplorerPage } from '@/pages/charts/ChartExplorerPage';
import { ServiceMapPage } from '@/pages/service-map/ServiceMapPage';
import { DashboardDetailPage } from '@/pages/dashboards/DashboardDetailPage';
import { DashboardSession } from '@/pages/dashboards/DashboardSession';
import { PanelRoutePage } from '@/pages/dashboards/PanelRoutePage';
import { DevicesPage } from '@/pages/iot/DevicesPage';
import { AnalyticsPage } from '@/pages/analytics/AnalyticsPage';
import { DashboardsPage } from '@/pages/dashboards/DashboardsPage';
import { RumPage } from '@/pages/rum/RumPage';
import { LogConsumptionPage } from '@/pages/log-consumption/LogConsumptionPage';
import { SettingsPage } from '@/pages/settings/SettingsPage';
import { RequireAuth } from './RequireAuth';

function LegacyRumRedirect() {
  const { search } = useLocation();
  return <Navigate to={`/sessions${search}`} replace />;
}

/** 路由表. */
export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/dashboards/:id/present', element: <RequireAuth><DashboardDetailPage present /></RequireAuth> },
  {
    element: <RequireAuth><AppLayout /></RequireAuth>,
    children: [
      { path: '/', element: <Navigate to="/overview" replace /> },
      { path: '/overview', element: <OverviewPage /> },
      { path: '/search', element: <SearchPage /> },
      { path: '/saved-searches', element: <SavedSearchesPage /> },
      // 已保存搜索打开页：路由参数注入同一 SearchPage 组件（打开即应用快照，保存即更新）
      { path: '/saved-searches/:savedId', element: <SavedSearchPage /> },
      { path: '/charts', element: <ChartExplorerPage /> },
      { path: '/service-map', element: <ServiceMapPage /> },
      { path: '/metrics', element: <Navigate to="/dashboards" replace /> },
      { path: '/iot', element: <DevicesPage /> },
      { path: '/analytics', element: <AnalyticsPage /> },
      { path: '/dashboards', element: <DashboardsPage /> },
      { path: '/dashboards/:id', element: <DashboardSession />, children: [
        { index: true, element: <DashboardDetailPage /> },
        { path: 'panels/new', element: <RequireAuth roles={['admin', 'write']}><PanelRoutePage /></RequireAuth> },
        { path: 'panels/:panelId/preview', element: <PanelRoutePage preview /> },
        { path: 'panels/:panelId/edit', element: <RequireAuth roles={['admin', 'write']}><PanelRoutePage /></RequireAuth> },
      ] },
      { path: '/sessions', element: <RumPage /> },
      { path: '/rum', element: <LegacyRumRedirect /> },
      { path: '/log-consumption', element: <LogConsumptionPage /> },
      { path: '/settings', element: <SettingsPage /> },
      // 日志/链路并入统一搜索页（数据源切换），旧地址带 source 参数重定向
      { path: '/logs', element: <Navigate to="/search?source=logs" replace /> },
      { path: '/traces', element: <Navigate to="/search?source=traces" replace /> },
      // 成员管理并入「系统设置 → 成员」tab（全员可见），旧 /users 链接重定向
      { path: '/users', element: <Navigate to="/settings?tab=members" replace /> },
    ],
  },
]);
