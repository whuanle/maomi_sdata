import type { EChartsOption } from 'echarts';
import type { BarDisplaySettings } from '@/api/chartTypes';
import { formatBarAxisValue, formatBarValue } from './barFormat';

export function barData(result: { columns: string[]; rows: unknown[][] }, total: string) {
  const index = result.columns.indexOf('value');
  const dimensions = result.columns.map((name, i) => ({ name, i })).filter(({ name }) => name !== 'value' && !name.startsWith('__') && name !== 'series');
  const used = new Set<string>();
  const data = result.rows.map((row) => {
    const key = JSON.stringify(dimensions.map(({ i }) => row[i]));
    const base = dimensions.map(({ i }) => row[i] == null ? '(null)' : String(row[i])).join(' / ') || total;
    let name = base;
    let suffix = 2;
    while (used.has(name)) name = base + ' (' + suffix++ + ')';
    used.add(name);
    const raw = row[index];
    const numeric = typeof raw === 'number' || (typeof raw === 'string' && raw.trim() !== '') ? Number(raw) : NaN;
    return { key, name, value: Number.isFinite(numeric) ? numeric : null };
  });
  return { data, invalid: index < 0 || (data.length > 0 && !data.some((item) => item.value !== null)) };
}

/** Explorer 与 Dashboard 共用，Bar 始终使用类别轴，时间列也只是普通类别. */
export function buildBarOption(result: { columns: string[]; rows: unknown[][] }, name: string, settings: BarDisplaySettings | undefined, border: string, showLegend = true): EChartsOption | null {
  const { data, invalid } = barData(result, name);
  if (invalid || data.length === 0) return null;
  const format = (value: unknown) => formatBarValue(typeof value === 'number' ? value : null, settings?.format);
  const legendVisible = showLegend && data.length > 1;
  const scrollable = data.length > 30;
  return {
    tooltip: { trigger: 'axis', renderMode: 'richText', confine: true, valueFormatter: format },
    legend: { show: legendVisible, bottom: 0, type: 'scroll' },
    grid: { left: 12, right: 20, top: 16, bottom: 16 + (legendVisible ? 24 : 0) + (scrollable ? 24 : 0), containLabel: true },
    xAxis: {
      type: 'category', boundaryGap: true, data: data.map((item) => item.name),
      axisLabel: { rotate: 0, margin: 12, width: 160, overflow: 'truncate', hideOverlap: true },
      axisTick: { alignWithLabel: true },
    },
    yAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatBarAxisValue(value, settings?.format) }, splitLine: { lineStyle: { color: border } } },
    dataZoom: scrollable ? [{ type: 'slider', xAxisIndex: 0, bottom: legendVisible ? 24 : 4, height: 14, startValue: 0, endValue: 29 }] : undefined,
    series: [{ name, type: 'bar', barWidth: '80%', data: data.map((item) => item.value) }],
  };
}
