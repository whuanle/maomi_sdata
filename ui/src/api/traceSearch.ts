import { searchSql } from './search';
import type { AnalyticsQueryResponse } from './types';

export type TraceRow = Record<string, unknown>;
export const SPAN_COLUMNS = 'trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource';

export function traceRows(response: AnalyticsQueryResponse): TraceRow[] {
  return response.rows.map((cells) => Object.fromEntries(response.columns.map((name, index) => [name, cells[index]])));
}

export async function loadSpan(row: TraceRow): Promise<TraceRow | null> {
  if (!row.trace_id || !row.span_id || !row.ts) return null;
  const date = new Date(String(row.ts));
  if (!Number.isFinite(date.getTime())) return null;
  const from = new Date(date);
  from.setUTCHours(0, 0, 0, 0);
  const response = await searchSql({
    sql: `SELECT ${SPAN_COLUMNS} FROM {{traces}} WHERE trace_id = $trace_id AND span_id = $span_id LIMIT 1`,
    from: from.toISOString(), to: new Date(from.getTime() + 86400000).toISOString(),
    rowLimit: 1, parameters: { trace_id: String(row.trace_id), span_id: String(row.span_id) },
  });
  return traceRows(response)[0] ?? null;
}

export async function loadTraceWindow(traceId: string, from: string, to: string) {
  const response = await searchSql({
    sql: `SELECT ${SPAN_COLUMNS} FROM {{traces}} WHERE trace_id = $trace_id AND ts >= $start AND ts < $end ORDER BY ts ASC LIMIT 10001`,
    from, to, rowLimit: 10000, parameters: { trace_id: traceId },
  });
  return { rows: traceRows(response), truncated: response.truncated };
}

export async function loadTraceLogs(traceId: string, from: string, to: string) {
  const response = await searchSql({
    sql: 'SELECT CAST(id AS VARCHAR) AS id, ts, service, severity, body, trace_id, span_id, attributes, resource FROM {{logs}} WHERE trace_id = $trace_id AND ts >= $start AND ts < $end ORDER BY ts ASC LIMIT 201',
    from, to, rowLimit: 200, parameters: { trace_id: traceId },
  });
  return { rows: traceRows(response), truncated: response.truncated };
}
