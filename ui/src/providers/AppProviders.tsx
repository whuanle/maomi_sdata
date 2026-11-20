import { useEffect, type ReactNode } from 'react';
import dayjs from 'dayjs';
import i18n from '@/i18n';
import { useAppStore } from '@/store/app';
import { Toaster } from '@/design-system';

interface AppProvidersProps {
  children: ReactNode;
}

/**
 * 应用 Provider：i18n 语言同步（含 dayjs locale 与 <html lang>）+ 全局 Toaster。
 * 主题桥在 store/app.ts：状态变更时同步把 data-theme 落到 <html>（v2 CSS 变量数据源）。
 */
export function AppProviders({ children }: AppProvidersProps) {
  const locale = useAppStore((state) => state.locale);

  useEffect(() => {
    void i18n.changeLanguage(locale);
    document.documentElement.lang = locale;
    // dayjs locale 跟随应用语言（相对时间/月份名等场景保持一致）
    dayjs.locale(locale === 'zh-CN' ? 'zh-cn' : 'en');
  }, [locale]);

  return (
    <>
      {children}
      <Toaster />
    </>
  );
}
