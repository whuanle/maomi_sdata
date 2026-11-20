import { useMemo } from 'react';
import type { EChartsOption } from 'echarts';
import { useTranslation } from 'react-i18next';
import { formatDateTime } from '@/utils/format';
import { Chart } from './Chart';
import { formatChartValue } from './chartFormat';

export interface TimeSeries {
  name: string;
  labels?: string;
  points: { timestampMs: number; value: number | null }[];
}

export interface TimeSeriesChartProps {
  series: TimeSeries[];
  type?: 'line' | 'area' | 'bar';
  unit?: string;
  showLegend?: boolean;
  zoom?: boolean;
}

/** 完整标签参与序列标识，避免简写图例让不同序列合并或一起切换. */
export function timeSeriesNames(series: TimeSeries[]): string[] {
  const used = new Set<string>();
  return series.map((item) => {
    let label = '';
    try {
      const entries = Object.entries(JSON.parse(item.labels || '{}') as Record<string, unknown>);
      label = entries.sort(([a], [b]) => a.localeCompare(b)).map(([key, value]) => `${key}=${String(value)}`).join(', ');
    } catch {
      label = item.labels || '';
    }

    const base = label ? `${item.name} {${label}}` : item.name;
    let name = base;
    let count = 2;
    while (used.has(name)) {
      name = `${base} (#${count++})`;
    }

    used.add(name);
    return name;
  });
}

/** 时间序列 option：缺失点保持缺口，堆叠不根据序列数量隐式启用. */
export function buildTimeSeriesOption({ series, type = 'line', unit, showLegend = true, zoom = false }: TimeSeriesChartProps): EChartsOption {
  const names = timeSeriesNames(series);
  const legend = showLegend && series.length > 1;
  return {
    animation: false,
    tooltip: {
      trigger: 'axis',
      renderMode: 'richText',
      valueFormatter: (value) => formatChartValue(typeof value === 'number' ? value : null, unit),
      formatter: (params) => {
        const items = Array.isArray(params) ? params : [params];
        const first = items[0]?.value;
        const timestamp = Array.isArray(first) ? first[0] : undefined;
        const header = typeof timestamp === 'number' ? formatDateTime(timestamp) : '';
        const rows = items.slice(0, 12).map((item) => {
          const name = String(item.seriesName ?? '').replace(/[{}\r\n]/g, '');
          const label = name.length > 27 ? `${name.slice(0, 24)}…` : name;
          const value = Array.isArray(item.value) ? item.value[1] : item.value;
          return `${label}  ${formatChartValue(typeof value === 'number' ? value : null, unit)}`;
        });
        if (items.length > 12) rows.push(`+${items.length - 12}`);
        return [header, ...rows].join('\n');
      },
    },
    legend: {
      show: legend,
      type: 'scroll',
      top: 0,
      left: 8,
      right: 8,
      itemWidth: 12,
      itemHeight: 3,
      formatter: (name: string) => name.length > 46 ? `${name.slice(0, 43)}…` : name,
    },
    grid: { left: 8, right: 16, top: legend ? 36 : 12, bottom: zoom ? 42 : 8, containLabel: true },
    xAxis: { type: 'time', splitNumber: 5, splitLine: { show: false }, axisLabel: { hideOverlap: true } },
    yAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatChartValue(value, unit) } },
    dataZoom: zoom ? [{ type: 'inside', filterMode: 'none' }, { type: 'slider', height: 18, bottom: 4 }] : undefined,
    series: series.map((item, index) => ({
      id: `series-${index}`,
      name: names[index],
      type: type === 'bar' ? 'bar' : 'line',
      showSymbol: false,
      connectNulls: false,
      lineStyle: { width: 1.5 },
      areaStyle: type === 'area' ? { opacity: 0.16 } : undefined,
      barMaxWidth: 28,
      data: item.points.map((point) => [point.timestampMs, point.value !== null && Number.isFinite(point.value) ? point.value : null]),
    })),
  };
}

/** 指标页、仪表盘与编辑预览共用的 ECharts 时间序列组件. */
export function TimeSeriesChart(props: TimeSeriesChartProps) {
  const { t } = useTranslation();
  const option = useMemo(() => buildTimeSeriesOption(props), [props.series, props.type, props.unit, props.showLegend, props.zoom]);
  if (!props.series.some((series) => series.points.some((point) => point.value !== null && Number.isFinite(point.value)))) {
    return <div className="flex h-full items-center justify-center text-sm text-muted">{t('dashboard.noData')}</div>;
  }

  return <Chart option={option} minHeight={0} />;
}
