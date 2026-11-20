import { get } from './client';
import type { SpanItem, TraceSummaryItem } from './types';

/** Trace 查询参数. */
export interface TraceQueryParams {
  start?: string;
  end?: string;
  service?: string;
  minDurationMs?: number;
  hasError?: boolean;
  limit?: number;
}

/** Trace 列表. */
export function queryTraces(params: TraceQueryParams) {
  return get<TraceSummaryItem[]>('/traces', params);
}

/** 完整 trace. */
export function getTrace(traceId: string) {
  return get<SpanItem[]>(`/traces/${traceId}`);
}
