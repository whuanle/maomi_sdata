import { Outlet, useMatch } from 'react-router';
import { cn } from '@/utils/cn';
import { AppSider } from './components/AppSider';

/** 主布局：248px 侧边栏 + 内容区（内容区独立滚动，HyperDX 式工作台骨架）. */
export function AppLayout() {
  const dashboardDetail = useMatch('/dashboards/:id');
  return (
    <div className="flex h-screen overflow-hidden bg-bg text-fg">
      <AppSider />
      <main className={cn('min-w-0 flex-1 overflow-auto', !dashboardDetail && 'p-6')}>
        <Outlet />
      </main>
    </div>
  );
}
