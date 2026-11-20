import { searchSql } from './search';
import type { TimeRange } from '@/design-system';

export interface ChartMetric { name: string; kinds: string[] }
export interface ChartMetricDetails { labels: Record<string, unknown>[]; truncated: boolean }

/** 与图表相同时间窗和冷热表展开通道；目录仅列实际存在的指标. */
export async function queryChartMetrics(range: TimeRange): Promise<{ items: ChartMetric[]; truncated: boolean }> {
  const result = await searchSql({ ...range, rowLimit: 10000, sql: 'SELECT name, string_agg(DISTINCT kind, \',\') AS kinds FROM {{metrics}} WHERE ts >= $start AND ts < $end GROUP BY name ORDER BY name LIMIT 10001' });
  const name = result.columns.indexOf('name');
  const kinds = result.columns.indexOf('kinds');
  return { items: name < 0 ? [] : result.rows.slice(0, 10000).map((row) => ({ name: String(row[name]), kinds: String(row[kinds] ?? '').split(',').filter(Boolean) })), truncated: result.truncated || result.rows.length > 10000 };
}

/** 属性详情有界抽样，名称经参数绑定；不将 labels 冒充完整 Resource 元数据. */
export async function queryChartMetricDetails(name: string, range: TimeRange): Promise<ChartMetricDetails> {
  const result = await searchSql({ ...range, rowLimit: 101, parameters: { metric_name: name }, sql: 'SELECT DISTINCT labels FROM {{metrics}} WHERE ts >= $start AND ts < $end AND name = $metric_name LIMIT 101' });
  const index = result.columns.indexOf('labels');
  const labels = index < 0 ? [] : result.rows.slice(0, 100).flatMap((row) => {
    try {
      const value: unknown = typeof row[index] === 'string' ? JSON.parse(row[index] as string) : row[index];
      return value && typeof value === 'object' && !Array.isArray(value) ? [value as Record<string, unknown>] : [];
    } catch { return []; }
  });
  return { labels, truncated: result.truncated || result.rows.length > 100 };
}
