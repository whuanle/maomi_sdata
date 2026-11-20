import {
  Bookmark,
  Cable,
  ChartSpline,
  ChevronDown,
  Code,
  Database,
  LayoutDashboard,
  LayoutGrid,
  LogOut,
  Moon,
  Network,
  Search,
  Settings,
  Sun,
  User,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router';
import { AppLogo } from '@/layouts/components/AppLogo';
import { DropdownMenu, Select, Tag } from '@/design-system';
import { useAppStore, type Locale } from '@/store/app';
import { useAuthStore, type SDataRole } from '@/store/auth';
import { cn } from '@/utils/cn';

interface NavItem {
  key: string;
  icon: LucideIcon;
  labelKey: string;
  path: string;
}

const mainNav: NavItem[] = [
  { key: 'overview', icon: LayoutDashboard, labelKey: 'menu.overview', path: '/overview' },
  { key: 'search', icon: Search, labelKey: 'menu.search', path: '/search' },
  { key: 'savedSearches', icon: Bookmark, labelKey: 'menu.savedSearches', path: '/saved-searches' },
  { key: 'charts', icon: ChartSpline, labelKey: 'menu.charts', path: '/charts' },
  { key: 'serviceMap', icon: Network, labelKey: 'menu.serviceMap', path: '/service-map' },
  { key: 'dashboards', icon: LayoutGrid, labelKey: 'menu.dashboards', path: '/dashboards' },
  { key: 'rum', icon: User, labelKey: 'menu.rum', path: '/sessions' },
  { key: 'iot', icon: Cable, labelKey: 'menu.iot', path: '/iot' },
  { key: 'analytics', icon: Code, labelKey: 'menu.analytics', path: '/analytics' },
  { key: 'logConsumption', icon: Database, labelKey: 'menu.logConsumption', path: '/log-consumption' },
  { key: 'settings', icon: Settings, labelKey: 'menu.settings', path: '/settings' },
];

/** 路由 → 菜单选中项：精确匹配 + 子路径前缀匹配（如 /saved-searches/:id 高亮已保存搜索）. */
function resolveSelectedKey(pathname: string): string {
  return mainNav.find((item) => pathname === item.path || pathname.startsWith(`${item.path}/`))?.key ?? '';
}

const localeOptions = [
  { label: '简体中文', value: 'zh-CN' },
  { label: 'English', value: 'en-US' },
];

/** 角色徽章语义色（admin 金 / write 蓝 / readonly 默认）. */
const roleTagTone: Record<SDataRole, 'warning' | 'info' | 'default'> = {
  admin: 'warning',
  write: 'info',
  readonly: 'default',
};

export function AppSider() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const themeKey = useAppStore((state) => state.themeKey);
  const setThemeKey = useAppStore((state) => state.setThemeKey);
  const locale = useAppStore((state) => state.locale);
  const setLocale = useAppStore((state) => state.setLocale);
  const username = useAuthStore((state) => state.username);
  const role = useAuthStore((state) => state.role);
  const clear = useAuthStore((state) => state.clear);

  const selectedKey = resolveSelectedKey(location.pathname);

  return (
    <aside className="sticky top-0 flex h-screen w-[248px] shrink-0 flex-col border-r border-border bg-surface">
      {/* 品牌区 */}
      <div
        className="flex cursor-pointer items-center gap-2.5 px-3 pt-3.5 pb-2"
        onClick={() => navigate('/overview')}
      >
        <AppLogo size={30} />
        <div className="min-w-0 flex-1">
          <div className="truncate text-[15px] leading-tight font-semibold text-fg">SData</div>
          <div className="truncate text-[11px] text-subtle">{t('app.subtitle')}</div>
        </div>
      </div>

      {/* 导航区（菜单定义与 pathToKey 就在本文件顶部） */}
      <nav aria-label="SData" className="min-h-0 flex-1 overflow-auto px-3 pt-2 pb-3">
        <div className="px-2.5 pt-2 pb-1.5 text-xs font-semibold tracking-wider text-subtle">
          {t('nav.platform')}
        </div>
        {mainNav.map((item) => {
          const active = item.key === selectedKey;
          const Icon = item.icon;
          return (
            <button
              key={item.key}
              type="button"
              aria-current={active ? 'page' : undefined}
              onClick={() => navigate(item.path)}
              className={cn(
                'mb-0.5 flex h-9 w-full cursor-pointer items-center gap-2.5 rounded-md px-3 text-left text-sm transition-colors',
                'focus-visible:outline-2 focus-visible:outline-primary',
                active ? 'bg-primary-soft font-medium text-primary' : 'text-muted hover:bg-surface-2 hover:text-fg',
              )}
            >
              <Icon className="size-4 shrink-0" aria-hidden />
              <span className="truncate">{t(item.labelKey)}</span>
            </button>
          );
        })}
      </nav>

      {/* 底部：用户 + 主题/语言 */}
      <div className="flex flex-col gap-2 border-t border-border px-3 pt-2.5 pb-3">
        <DropdownMenu.Root>
          <DropdownMenu.Trigger asChild>
            <button
              type="button"
              className="flex cursor-pointer items-center gap-2 rounded-md p-1.5 text-left transition-colors hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-primary"
            >
              <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-surface-3 text-sm font-semibold text-muted">
                {username?.charAt(0)?.toUpperCase() ?? 'U'}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[13px] font-medium text-fg">{username ?? '-'}</span>
                <Tag tone={role ? roleTagTone[role] : 'default'} className="mt-0.5">
                  {t(`role.${role ?? 'readonly'}`)}
                </Tag>
              </span>
              <ChevronDown className="size-3.5 shrink-0 text-faint" aria-hidden />
            </button>
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="start" side="top">
            <DropdownMenu.Item
              icon={<LogOut />}
              onSelect={() => {
                clear();
                navigate('/login');
              }}
            >
              {t('common.logout')}
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Root>

        <div className="flex items-center gap-1.5">
          {/* 亮/暗切换（v2 双主题分段控件，替代 8 套预设选择器） */}
          <div className="flex h-7 w-16 items-center gap-0.5 rounded-md bg-surface-2 p-0.5" role="group" aria-label="theme">
            <button
              type="button"
              aria-pressed={themeKey === 'light'}
              title={t('common.light')}
              onClick={() => setThemeKey('light')}
              className={cn(
                'flex h-6 w-7 cursor-pointer items-center justify-center rounded-sm transition-colors',
                themeKey === 'light' ? 'bg-surface text-warning shadow-[var(--c-shadow-sm)]' : 'text-subtle hover:text-fg',
              )}
            >
              <Sun className="size-3.5" aria-hidden />
            </button>
            <button
              type="button"
              aria-pressed={themeKey === 'dark'}
              title={t('common.dark')}
              onClick={() => setThemeKey('dark')}
              className={cn(
                'flex h-6 w-7 cursor-pointer items-center justify-center rounded-sm transition-colors',
                themeKey === 'dark' ? 'bg-surface text-info shadow-[var(--c-shadow-sm)]' : 'text-subtle hover:text-fg',
              )}
            >
              <Moon className="size-3.5" aria-hidden />
            </button>
          </div>
          <Select
            aria-label="locale"
            className="h-7 flex-1 min-w-0 text-xs"
            value={locale}
            options={localeOptions}
            onValueChange={(value) => setLocale(value as Locale)}
          />
        </div>
      </div>
    </aside>
  );
}
