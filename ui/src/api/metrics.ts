import { get, post } from './client';
import type { MetricSeriesItem } from './types';

/** 指标名列表. */
export function listMetricNames() {
  return get<string[]>('/metrics');
}

/** 指标序列查询参数. */
export interface MetricSeriesQuery {
  name: string;
  labels?: Record<string, string>;
  start?: string;
  end?: string;
  aggregation?: string;
  stepSeconds?: number;
}

/** 指标序列查询. */
export function queryMetricSeries(query: MetricSeriesQuery) {
  return post<{ series: MetricSeriesItem[] }>('/metrics/query', query);
}
