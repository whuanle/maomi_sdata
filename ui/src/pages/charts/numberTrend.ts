import type { NumberResult } from './numberTypes';
import { numberValue } from './numberFormat';
import { resolveGranularity } from './buildChartSql';

const bucketMs = { '1m': 60000, '5m': 300000, '15m': 900000, '30m': 1800000, '1h': 3600000, '6h': 21600000, '1d': 86400000 };

/** DuckDB TIMESTAMP 无偏移字符串按存储约定解释为 UTC. */
function timestamp(value: unknown): number {
  if (typeof value !== 'string') return NaN;
  return Date.parse(/[zZ]$|[+-]\d\d:\d\d$/.test(value) ? value : value.replace(' ', 'T') + 'Z');
}

export function numberTrendPoints(result: NumberResult | null, id: string | undefined, label: string, single: boolean, range?: { from: string; to: string }): [number, number | null][] {
  if (!result || !range) return [];
  const timeIndex = result.columns.indexOf('ts_bucket');
  const valueIndex = result.columns.indexOf('value');
  const idIndex = result.columns.indexOf('__series_id');
  const nameIndex = result.columns.indexOf('series');
  if (timeIndex < 0 || valueIndex < 0) return [];
  const from = Date.parse(range.from);
  const to = Date.parse(range.to);
  const step = bucketMs[resolveGranularity('auto', to - from)];
  const points = result.rows
    .filter((row) => idIndex >= 0 ? String(row[idIndex]) === id : nameIndex >= 0 ? String(row[nameIndex]) === label : single)
    .map((row): [number, number | null] => [timestamp(row[timeIndex]), numberValue(row[valueIndex])])
    .filter(([time]) => Number.isFinite(time) && time < to && time + step > from)
    .sort((a, b) => a[0] - b[0]);
  if (points.filter(([, value]) => value !== null).length < 2) return [];
  const output: [number, number | null][] = [];
  for (const point of points) {
    const previous = output[output.length - 1];
    if (previous && point[0] - previous[0] > step * 1.5) output.push([previous[0] + step, null]);
    output.push(point);
  }
  return output;
}
