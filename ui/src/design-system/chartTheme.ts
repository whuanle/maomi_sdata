import type { EChartsOption } from 'echarts';
import { chartColors, fontFamily, type CssThemeColors } from './theme';

/** 图表组件主题默认值；显式 option 优先，数组形式的坐标轴/图例亦逐项合并. */
export function withChartTheme(option: EChartsOption, colors: CssThemeColors): EChartsOption {
  const axis = {
    axisLabel: { color: colors.textSecondary, fontFamily, fontSize: 11 },
    nameTextStyle: { color: colors.textSecondary },
    axisLine: { lineStyle: { color: colors.border } },
    axisTick: { show: false },
    splitLine: { lineStyle: { color: colors.border, type: 'dashed' } },
    axisPointer: { label: { backgroundColor: colors.surface2, color: colors.text } },
  };
  const defaults = {
    color: chartColors,
    textStyle: { color: colors.text, fontFamily },
    legend: {
      textStyle: { color: colors.textSecondary, fontSize: 11 },
      pageTextStyle: { color: colors.textSecondary },
      pageIconColor: colors.textSecondary,
      pageIconInactiveColor: colors.border,
      inactiveColor: colors.textFaint,
    },
    tooltip: {
      backgroundColor: colors.surface,
      borderColor: colors.border,
      textStyle: { color: colors.text, fontFamily, fontSize: 12 },
      confine: true,
    },
    visualMap: { textStyle: { color: colors.textSecondary, fontFamily, fontSize: 11 } },
    title: { textStyle: { color: colors.textSecondary, fontFamily } },
    xAxis: axis,
    yAxis: axis,
    dataZoom: {
      textStyle: { color: colors.textSecondary },
      borderColor: colors.border,
      backgroundColor: colors.surface,
      fillerColor: colors.border,
      handleStyle: { color: colors.primary },
      moveHandleStyle: { color: colors.primary },
    },
  };
  // 只补充调用方已有的组件，避免给饼图添加无数据坐标轴.
  const result: Record<string, unknown> = {
    ...option,
    color: option.color ?? defaults.color,
    textStyle: merge(defaults.textStyle, option.textStyle),
  };
  for (const key of ['legend', 'tooltip', 'xAxis', 'yAxis', 'dataZoom', 'visualMap', 'title'] as const) {
    const value = option[key];
    if (value !== undefined) {
      result[key] = Array.isArray(value)
        ? value.map((item) => merge(defaults[key], item))
        : merge(defaults[key], value);
    }
  }

  return result as EChartsOption;
}

function merge(base: unknown, override: unknown): unknown {
  if (!override || typeof override !== 'object' || Array.isArray(override)) {
    return override === undefined ? base : override;
  }

  const result = { ...(base as Record<string, unknown>) };
  for (const [key, value] of Object.entries(override)) {
    result[key] = value && typeof value === 'object' && !Array.isArray(value)
      ? merge(result[key] ?? {}, value)
      : value;
  }

  return result;
}
