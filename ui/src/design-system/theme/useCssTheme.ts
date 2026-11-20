import { useMemo } from 'react';
import { useAppStore } from '@/store/app';

export interface CssThemeColors {
  mode: 'light' | 'dark';
  /** 正文/图表轴文字色 */
  text: string;
  /** 次级文字色（图表副标题/分隔说明） */
  textSecondary: string;
  /** 弱文字色（图表网格线常用） */
  textFaint: string;
  border: string;
  surface: string;
  surface2: string;
  bg: string;
  primary: string;
  accent: string;
  success: string;
  warning: string;
  error: string;
}

function readVar(target: CSSStyleDeclaration, name: string, fallback: string): string {
  const value = target.getPropertyValue(name).trim();
  return value || fallback;
}

/**
 * 从 CSS 变量读取主题色（v2 组件层取色唯一通道，替代 antd useToken）。
 * 依赖 store 的 themeKey 触发重渲，切主题后 echarts 等 JS 消费方自动跟随。
 * 注意：首次渲染时 AppProviders 的主题桥 effect 可能尚未写入 data-theme，
 * 此处以 store 值为准确保亮暗正确（变量组在 index.css 中两套齐备）。
 */
export function useCssTheme(): CssThemeColors {
  const themeKey = useAppStore((state) => state.themeKey);

  return useMemo(() => {
    const style = getComputedStyle(document.documentElement);
    return {
      mode: themeKey,
      text: readVar(style, '--c-fg', '#f2f2f2'),
      textSecondary: readVar(style, '--c-fg-muted', '#b3b6bd'),
      textFaint: readVar(style, '--c-fg-faint', '#626266'),
      border: readVar(style, '--c-border', '#333336'),
      surface: readVar(style, '--c-surface', '#262628'),
      surface2: readVar(style, '--c-surface-2', '#2e2e30'),
      bg: readVar(style, '--c-bg', '#1c1c1e'),
      primary: readVar(style, '--c-primary', '#7a6fe0'),
      accent: readVar(style, '--c-accent', themeKey === 'dark' ? '#2fb380' : '#0e8a5f'),
      success: readVar(style, '--c-success', '#22c55e'),
      warning: readVar(style, '--c-warning', '#f59e0b'),
      error: readVar(style, '--c-error', '#ef4444'),
    };
  }, [themeKey]);
}
