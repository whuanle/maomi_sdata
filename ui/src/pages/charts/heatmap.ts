import dayjs from 'dayjs';
import type { EChartsOption } from 'echarts';
import type { HeatmapConfig } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { formatDurationUs } from '@/utils/format';
import { bindChartParameter } from './formula';

export const DEFAULT_HEATMAP: HeatmapConfig = { field: 'duration_us', bins: 60, scale: 'log', colorScale: 'log' };
export const HEATMAP_COLORS = ['#299c60', '#6cbe45', '#bddb38', '#f4cc32', '#f59a23', '#e85920'];
export const HEATMAP_GRID_LIMIT = 9600;

export function validHeatmapConfig(value: HeatmapConfig): boolean {
  return ['duration_us', 'value'].includes(value.field) && Number.isInteger(value.bins) && value.bins >= 10 && value.bins <= 80
    && (value.scale === undefined || ['linear', 'log'].includes(value.scale))
    && (value.colorScale === undefined || ['linear', 'log'].includes(value.colorScale));
}

const inverse = (value: number) => Math.sign(value) * Math.expm1(Math.abs(value) * Math.LN10);

/** 新分布按实际窗口自适应；旧配置继续采用保存的线性分桶与时间粒度. */
export function buildHeatmapSql(
  domain: string, config: HeatmapConfig, where: string, interval: string, parameters: SearchSqlParameters,
): string {
  if (!((domain === 'traces' && config.field === 'duration_us') || (domain === 'metrics' && config.field === 'value'))) {
    throw new Error('热力图仅支持链路耗时或指标数值');
  }
  if (!validHeatmapConfig(config)) throw new Error('热力图分桶数量必须在 10 到 80 之间且尺度有效');
  const bins = bindChartParameter(parameters, config.bins);
  const log = config.scale === 'log';
  const sample = log ? 'sign(sample) * log10(1 + abs(sample))' : 'sample';
  const min = log ? 'floor(min(position))' : 'min(position)';
  const max = log ? 'ceil(max(position))' : 'max(position)';
  const slots = bindChartParameter(parameters, Math.min(240, Math.floor(HEATMAP_GRID_LIMIT / config.bins)) - 1);
  const adaptive = "INTERVAL '1 second' * greatest(1, ceil(epoch(CAST($end AS TIMESTAMP) - CAST($start AS TIMESTAMP)) / " + slots + '))';
  const step = config.scale !== undefined ? adaptive : `greatest(${interval}, ${adaptive})`;
  const edge = (expr: string) => log ? `sign(${expr}) * (pow(10, abs(${expr})) - 1)` : expr;
  return `WITH filtered AS (
  SELECT ts, CAST("${config.field}" AS DOUBLE) AS sample
  FROM {{${domain}}}
  WHERE ts >= $start AND ts < $end${where.trim() ? ` AND (${where})` : ''}
    AND "${config.field}" IS NOT NULL AND isfinite(CAST("${config.field}" AS DOUBLE))${config.field === 'duration_us' ? ' AND duration_us >= 0' : ''}
), positioned AS (
  SELECT ts, sample, ${sample} AS position FROM filtered
), bounds AS (
  SELECT ${min} AS lo, CASE WHEN ${max} > ${min} THEN ${max}
    ELSE ${min} + greatest(abs(${min}) * 0.01, 1) END AS hi, max(sample) AS sample_max FROM positioned
), settings AS (
  SELECT ${step} AS step
), bucketed AS (
  SELECT time_bucket(step, ts) AS ts_bucket,
    least(${bins} - 1, greatest(0, CAST(floor((position - lo) / (hi - lo) * ${bins}) AS INTEGER))) AS y_bucket,
    lo, hi, sample_max, epoch(step) * 1000 AS __bucket_ms
  FROM positioned CROSS JOIN bounds CROSS JOIN settings
)
SELECT ts_bucket, y_bucket, ${edge(`(lo + (hi - lo) * y_bucket / ${bins})`)} AS bucket_min,
  ${edge(`(lo + (hi - lo) * (y_bucket + 1) / ${bins})`)} AS bucket_max,
  count(*) AS value, __bucket_ms, lo AS __scale_min, hi AS __scale_max, sample_max AS __sample_max
FROM bucketed
GROUP BY ts_bucket, y_bucket, lo, hi, sample_max, __bucket_ms
ORDER BY ts_bucket, y_bucket
LIMIT 10001`;
}

export interface HeatmapLabels {
  low: string;
  high: string;
  count: string;
  duration: string;
  value: string;
  noData: string;
}

type Result = { columns: string[]; rows: unknown[][] };
type Range = { from: string; to: string };
export interface HeatmapCell extends Range {
  min: number;
  max: number;
  includeMax: boolean;
  count: number;
  x: number;
  y: number;
  scaleMin?: number;
  scaleMax?: number;
}
export interface HeatmapGrid {
  times: number[];
  step: number;
  bounds: [number, number][];
  cells: HeatmapCell[];
}

/** 显式分布结果与旧类别热力图分开识别，避免更改已保存面板的含义. */
export function isDistributionHeatmap(columns: string[]): boolean {
  return ['ts_bucket', 'y_bucket', 'bucket_min', 'bucket_max', 'value', '__bucket_ms'].every((column) => columns.includes(column));
}

export function distributionGrid(result: Result, range: Range, config: HeatmapConfig): HeatmapGrid | null {
  if (!isDistributionHeatmap(result.columns) || !validHeatmapConfig(config)) return null;
  const at = (row: unknown[], name: string) => row[result.columns.indexOf(name)];
  const rows = result.rows.filter((row) => ['y_bucket', 'bucket_min', 'bucket_max', 'value', '__bucket_ms']
    .every((name) => at(row, name) != null && Number.isFinite(Number(at(row, name))))
    && dayjs(String(at(row, 'ts_bucket'))).isValid()
    && Number(at(row, '__bucket_ms')) > 0 && Number(at(row, 'value')) > 0
    && Number.isInteger(Number(at(row, 'y_bucket'))) && Number(at(row, 'y_bucket')) >= 0 && Number(at(row, 'y_bucket')) < config.bins);
  const from = dayjs(range.from).valueOf(), to = dayjs(range.to).valueOf();
  if (!Number.isFinite(from) || !Number.isFinite(to) || from >= to) return null;
  if (!rows.length) return { times: [], step: 0, bounds: [], cells: [] };
  const first = rows[0], step = Number(at(first, '__bucket_ms'));
  const anchor = dayjs(String(at(first, 'ts_bucket'))).valueOf();
  const start = anchor + Math.floor((from - anchor) / step) * step;
  const count = Math.ceil((to - start) / step);
  if (count < 1 || count * config.bins > HEATMAP_GRID_LIMIT) return null;
  const times = Array.from({ length: count }, (_, index) => start + index * step);
  const log = config.scale === 'log';
  const width = Number(at(first, 'bucket_max')) - Number(at(first, 'bucket_min'));
  const oldLo = Number(at(first, 'bucket_min')) - Number(at(first, 'y_bucket')) * width;
  const lo = result.columns.includes('__scale_min') ? Number(at(first, '__scale_min')) : oldLo;
  const hi = result.columns.includes('__scale_max') ? Number(at(first, '__scale_max')) : oldLo + width * config.bins;
  if (!Number.isFinite(lo) || !Number.isFinite(hi) || !(hi > lo) || (log && !result.columns.includes('__scale_min'))) return null;
  const raw = (position: number) => log ? inverse(position) : position;
  const bounds: [number, number][] = Array.from({ length: config.bins }, (_, index) => [
    raw(lo + (hi - lo) * index / config.bins), raw(lo + (hi - lo) * (index + 1) / config.bins),
  ]);
  // 使用数据库计算的边界，避免浮点逆变换误差改变下钻归属.
  for (const row of rows) bounds[Number(at(row, 'y_bucket'))] = [Number(at(row, 'bucket_min')), Number(at(row, 'bucket_max'))];
  const cells = rows.filter((row) => {
    const time = dayjs(String(at(row, 'ts_bucket'))).valueOf();
    return time < to && time + step > from;
  }).map((row): HeatmapCell => {
    const x = Math.round((dayjs(String(at(row, 'ts_bucket'))).valueOf() - start) / step);
    const y = Number(at(row, 'y_bucket'));
    return {
      x, y, from: dayjs(Math.max(from, times[x])).toISOString(), to: dayjs(Math.min(to, times[x] + step)).toISOString(),
      min: bounds[y][0], max: bounds[y][1], includeMax: y === config.bins - 1,
      count: Number(at(row, 'value')), scaleMin: lo, scaleMax: hi,
    };
  }).filter((cell) => cell.x >= 0 && cell.x < count);
  return { times, step, bounds, cells };
}

export function heatmapZoomRange(grid: HeatmapGrid, range: Range, start: number, end: number): Range | null {
  if (!Number.isFinite(start) || !Number.isFinite(end) || !grid.times.length || end <= start) return null;
  const lo = Math.max(0, Math.floor(start)), hi = Math.min(grid.times.length - 1, Math.ceil(end));
  if (lo > hi) return null;
  const from = Math.max(dayjs(range.from).valueOf(), grid.times[lo]);
  const to = Math.min(dayjs(range.to).valueOf(), grid.times[hi] + grid.step);
  if (from >= to || (from === dayjs(range.from).valueOf() && to === dayjs(range.to).valueOf())) return null;
  return { from: dayjs(from).toISOString(), to: dayjs(to).toISOString() };
}

export function buildHeatmapSampleSql(sql: string, config: HeatmapConfig, cell: HeatmapCell, parameters: SearchSqlParameters): string {
  const min = bindChartParameter(parameters, cell.min), max = bindChartParameter(parameters, cell.max);
  const from = bindChartParameter(parameters, cell.from), to = bindChartParameter(parameters, cell.to);
  const fullSql = sql.replace(/\nORDER BY ts DESC\nLIMIT 500$/, '');
  let predicate = `"${config.field}" >= ${min} AND "${config.field}" ${cell.includeMax ? '<=' : '<'} ${max}`;
  if (cell.scaleMin !== undefined && cell.scaleMax !== undefined) {
    const lo = bindChartParameter(parameters, cell.scaleMin), hi = bindChartParameter(parameters, cell.scaleMax);
    const bins = bindChartParameter(parameters, config.bins), bucket = bindChartParameter(parameters, cell.y);
    const sample = `CAST("${config.field}" AS DOUBLE)`;
    const position = config.scale === 'log' ? `sign(${sample}) * log10(1 + abs(${sample}))` : sample;
    predicate = `isfinite(${sample})${config.field === 'duration_us' ? ' AND duration_us >= 0' : ''}
      AND least(${bins} - 1, greatest(0, CAST(floor((${position} - ${lo}) / (${hi} - ${lo}) * ${bins}) AS INTEGER))) = ${bucket}`;
  }
  return `SELECT * FROM (${fullSql}) AS matched WHERE ts >= CAST(${from} AS TIMESTAMP) AND ts < CAST(${to} AS TIMESTAMP)
    AND ${predicate}
    ORDER BY ts DESC LIMIT 500`;
}

export function buildDistributionOption(
  result: Result, range: Range, config: HeatmapConfig, showLegend: boolean, labels: HeatmapLabels,
  interactive = false, selected?: HeatmapCell | null,
): EChartsOption | null {
  const grid = distributionGrid(result, range, config);
  if (!grid) return null;
  const from = dayjs(range.from).valueOf(), to = dayjs(range.to).valueOf();
  if (!grid.cells.length) return {
    title: { text: labels.noData, left: 'center', top: 'middle', textStyle: { fontSize: 13, fontWeight: 'normal' } },
    xAxis: { type: 'time', min: from, max: to }, yAxis: { type: 'value', name: config.field === 'duration_us' ? labels.duration : labels.value },
    series: [],
  };
  const format = (value: number) => config.field === 'duration_us' ? formatDurationUs(value) : Number(value.toPrecision(4)).toLocaleString();
  const yLabels = grid.bounds.map(([min, max]) => `${format(min)} – ${format(max)}`);
  const color = (value: number) => config.colorScale === 'log' ? Math.log1p(value) : value;
  const data = grid.cells.map((cell) => ({
    value: [cell.x, cell.y, color(cell.count), cell.count],
    itemStyle: selected?.x === cell.x && selected?.y === cell.y ? { borderWidth: 2, borderColor: '#ffffff' } : undefined,
  }));
  return {
    animation: false,
    grid: { left: 68, right: 16, top: showLegend ? 38 : 16, bottom: interactive ? 48 : 28, containLabel: false },
    tooltip: { trigger: 'item', renderMode: 'richText', confine: true, formatter: (params) => {
      const point = (Array.isArray(params) ? params[0] : params).value as number[];
      const cell = grid.cells.find((item) => item.x === point[0] && item.y === point[1]);
      if (!cell) return '';
      return `${dayjs(cell.from).format('YYYY-MM-DD HH:mm:ss')} – ${dayjs(cell.to).format('HH:mm:ss')}\n${yLabels[cell.y]}\n${labels.count}: ${cell.count.toLocaleString()}`;
    } },
    xAxis: { type: 'category', data: grid.times, axisLabel: { hideOverlap: true, interval: Math.max(0, Math.ceil(grid.times.length / 8) - 1),
      formatter: (time: string) => dayjs(Number(time)).format(to - from > 86400000 ? 'MM-DD HH:mm' : to - from < 600000 ? 'HH:mm:ss' : 'HH:mm') },
      splitLine: { show: true, interval: Math.max(0, Math.ceil(grid.times.length / 8) - 1), lineStyle: { type: 'dashed', opacity: 0.4 } } },
    yAxis: { type: 'category', data: grid.bounds.map(([min]) => format(Number(min.toPrecision(2)))), axisLabel: { interval: Math.max(0, Math.ceil(config.bins / 8) - 1), width: 60, overflow: 'truncate' },
      splitLine: { show: true, interval: Math.max(0, Math.ceil(config.bins / 8) - 1), lineStyle: { type: 'dashed', opacity: 0.4 } }, splitArea: { show: false } },
    visualMap: { show: showLegend, min: color(1), max: Math.max(color(2), ...grid.cells.map((cell) => color(cell.count))), dimension: 2,
      orient: 'horizontal', right: 0, top: 0, itemWidth: 8, itemHeight: 100, text: [labels.high, labels.low], calculable: false, inRange: { color: HEATMAP_COLORS } },
    brush: interactive ? { xAxisIndex: 0, brushType: 'lineX', brushMode: 'single', transformable: false, toolbox: [] } : undefined,
    dataZoom: interactive ? [{ type: 'inside', xAxisIndex: 0, filterMode: 'none', zoomOnMouseWheel: 'shift', moveOnMouseMove: false }] : undefined,
    toolbox: interactive ? { right: 16, top: showLegend ? 18 : 0, feature: { brush: { type: ['lineX'] } } } : undefined,
    series: [{ type: 'heatmap', data, emphasis: { itemStyle: { borderWidth: 1, borderColor: '#ffffff' } }, progressive: 0 }],
  };
}
