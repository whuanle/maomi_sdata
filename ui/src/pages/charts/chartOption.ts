import dayjs from 'dayjs';
import type { PieDisplaySettings } from '@/api/chartTypes';
import { pieData } from './pieDisplay';
import { buildBarOption } from './barDisplay';
import { formatBarValue } from './barFormat';
import type { EChartsOption } from 'echarts';
import { chartColors } from '@/design-system/theme';
import { seriesLabel, type ChartSeries, type ChartType } from './buildChartSql';

/** Explorer 与仪表盘共用的长表结果渲染；标识与展示名分离. */
export function buildChartOption(
  result: { columns: string[]; rows: unknown[][] },
  type: ChartType,
  range: { from: string; to: string },
  series: (ChartSeries | (Pick<ChartSeries, 'id' | 'alias'> & { label?: string }))[],
  showLegend: boolean,
  display: 'line' | 'bar',
  border: string,
  pieSettings?: PieDisplaySettings,
): EChartsOption | null {
  const fallback = series[0] ? 'aggFunc' in series[0] ? seriesLabel(series[0]) : series[0].label || series[0].alias || 'value' : 'value';
  if (type === 'bar') return buildBarOption(result, fallback, pieSettings, border, showLegend);
  const { columns, rows } = result;
  const valueIndex = columns.indexOf('value');
  if (valueIndex < 0) return null;
  const bucketIndex = columns.indexOf('ts_bucket');
  const idIndex = columns.indexOf('__series_id');
  const seriesIndex = columns.indexOf('series');
  const dimensions = columns.map((name, index) => ({ name, index }))
    .filter(({ name }) => !['ts_bucket', 'value'].includes(name) && !name.startsWith('__'));
  const groupDimensions = dimensions.filter(({ name }) => name !== 'series');
  const label = (row: unknown[]) => dimensions.map(({ index }) => String(row[index] ?? '-')).join(' / ') || fallback;
  const tuple = (row: unknown[]) => groupDimensions.map(({ index }) => row[index]);
  const identity = (row: unknown[], includeGroups: boolean) => JSON.stringify([
    idIndex >= 0 ? row[idIndex] : seriesIndex >= 0 ? row[seriesIndex] : fallback,
    ...(includeGroups ? tuple(row) : []),
  ]);
  const value = (row: unknown[]) => {
    const numeric = row[valueIndex] == null ? NaN : Number(row[valueIndex]);
    return Number.isFinite(numeric) ? numeric : null;
  };

  if (type === 'pie') {
    const { data, invalid } = pieData(result, fallback, pieSettings);
    if (invalid || !data.some((item) => item.value > 0)) return null;
    return {
      tooltip: { trigger: 'item', renderMode: 'richText', confine: true, formatter: (params) => {
        const item = Array.isArray(params) ? params[0] : params;
        return `${item.name}\n${formatBarValue(Number(item.value), pieSettings?.format)} (${item.percent ?? 0}%)`;
      } },
      legend: { show: showLegend, type: 'scroll', bottom: 0, icon: 'circle', formatter: (name) => `${name}  ${formatBarValue(data.find((item) => item.name === name)?.value, pieSettings?.format)}` },
      series: [{ type: 'pie', radius: ['42%', '70%'], center: ['50%', showLegend ? '44%' : '50%'], stillShowZeroSum: false,
        label: { show: false }, emphasis: { scale: true, scaleSize: 5 }, data }],
    };
  }

  const buckets = [...new Set(rows.map((row) => String(row[bucketIndex])))].sort();
  if (type === 'heatmap') {
    const keys = [...new Set(rows.map((row) => identity(row, true)))];
    const labels = keys.map((key) => label(rows.find((row) => identity(row, true) === key)!));
    const data = rows.filter((row) => value(row) !== null).map((row) => [buckets.indexOf(String(row[bucketIndex])), keys.indexOf(identity(row, true)), value(row)!]);
    return {
      tooltip: { position: 'top', renderMode: 'richText' },
      grid: { left: 90, right: 16, top: 12, bottom: 56 },
      xAxis: { type: 'category', data: buckets.map((raw) => dayjs(raw).format('MM-DD HH:mm:ss')) },
      yAxis: { type: 'category', data: labels },
      visualMap: { min: Math.min(0, ...data.map((point) => point[2])), max: Math.max(1, ...data.map((point) => point[2])), calculable: true, orient: 'horizontal', left: 'center', bottom: 0 },
      series: [{ type: 'heatmap', data }],
    };
  }

  const grouped = new Map<string, { name: string; points: Map<string, number | null> }>();
  const categoryOf = (row: unknown[]) => JSON.stringify(tuple(row));
  const categories = bucketIndex >= 0 ? buckets : [...new Set(rows.map(categoryOf))];
  const categoryLabels = categories.map((category) => {
    const row = rows.find((candidate) => categoryOf(candidate) === category);
    return row ? tuple(row).map((item) => String(item ?? '-')).join(' / ') || '-' : '-';
  });
  for (const row of rows) {
    const key = identity(row, bucketIndex >= 0);
    const group = grouped.get(key) ?? {
      name: bucketIndex >= 0 ? label(row) : String(seriesIndex >= 0 ? row[seriesIndex] : fallback),
      points: new Map<string, number | null>(),
    };
    group.points.set(bucketIndex >= 0 ? String(row[bucketIndex]) : categoryOf(row), value(row));
    grouped.set(key, group);
  }
  return {
    tooltip: { trigger: 'axis', renderMode: 'richText' },
    legend: { show: showLegend, bottom: 0, type: 'scroll' },
    grid: { left: 56, right: 16, top: 16, bottom: showLegend ? 48 : 28 },
    toolbox: bucketIndex >= 0 ? { feature: { dataZoom: { yAxisIndex: 'none' } }, right: 16 } : undefined,
    dataZoom: bucketIndex >= 0 ? [{ type: 'inside', filterMode: 'none', zoomOnMouseWheel: 'shift', moveOnMouseMove: false }] : undefined,
    xAxis: bucketIndex >= 0
      ? { type: 'time', min: dayjs(range.from).valueOf(), max: dayjs(range.to).valueOf(), axisLabel: { formatter: (raw: number) => dayjs(raw).format('MM-DD HH:mm:ss') } }
      : { type: 'category', data: categoryLabels },
    yAxis: { type: 'value', splitLine: { lineStyle: { color: border } } },
    series: [...grouped].map(([id, { name, points }], index) => ({
      id, name, type: display, connectNulls: false,
      showSymbol: false, barMaxWidth: 28, itemStyle: { color: chartColors[index % chartColors.length] },
      data: categories.map((category) => bucketIndex >= 0
        ? [dayjs(category).valueOf(), points.get(category) ?? null]
        : points.get(category) ?? null),
    })),
  };
}
