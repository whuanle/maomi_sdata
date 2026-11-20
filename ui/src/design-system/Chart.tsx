import { useEffect, useRef } from 'react';
import * as echarts from 'echarts';
import { useTranslation } from 'react-i18next';
import { useCssTheme } from '@/design-system/theme';
import { withChartTheme } from './chartTheme';

/** echarts 事件表：事件名 → handler（如 { click: (params) => … }），身份变化时重绑. */
export type ChartEventMap = Record<string, (params: echarts.ECElementEvent) => void>;

/**
 * echarts 图表 React 封装：自动初始化、更新、销毁与自适应。
 * 默认注入主题色板与文字色——颜色读 CSS 变量（useCssTheme），切主题自动跟随，
 * 页面可经 option 显式覆盖。
 */
export function Chart({ option, style, theme, onEvents, minHeight = 200 }: { option: echarts.EChartsOption; style?: React.CSSProperties; theme?: string; onEvents?: ChartEventMap; minHeight?: number }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | undefined>(undefined);
  const cssTheme = useCssTheme();
  const { i18n } = useTranslation();

  useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    const chart = echarts.init(containerRef.current, theme);
    chartRef.current = chart;
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(containerRef.current);
    return () => {
      observer.disconnect();
      chart.dispose();
      chartRef.current = undefined;
    };
  }, [theme]);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart || !onEvents) {
      return;
    }

    for (const [name, handler] of Object.entries(onEvents)) {
      // echarts on 的 handler 参数类型为 unknown，包一层收敛为 ECElementEvent
      chart.on(name, (args: unknown) => handler(args as echarts.ECElementEvent));
    }

    return () => {
      for (const name of Object.keys(onEvents)) {
        chart.off(name);
      }
    };
  }, [onEvents, theme]);

  useEffect(() => {
    chartRef.current?.setOption(withChartTheme(option, cssTheme), true);
  }, [option, cssTheme, i18n.language, theme]);

  // 显式传入 height 时不再叠加默认 minHeight（CSS min-height 会压过 height，导致容器溢出）
  return (
    <div
      ref={containerRef}
      style={{ width: '100%', height: '100%', minHeight: style?.height === undefined ? minHeight : undefined, ...style }}
    />
  );
}
