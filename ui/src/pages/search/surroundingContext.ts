import type { SearchDomain, SearchSqlParameters } from '@/api/types';
import { fieldExpression } from './lucene';
import { formatJsonField, type JsonSegment } from './jsonFields';
import { timestampUs } from './traceTimeline';
import type { DetailRow } from './detailShared';

export const CONTEXT_LIMIT = 100;
export const CONTEXT_MAX_MATCHES = 24;
export const CONTEXT_DEFAULT_WINDOW_MS = 30_000;
export const CONTEXT_WINDOWS = [
  { label: '100ms', ms: 100 }, { label: '500ms', ms: 500 },
  { label: '1s', ms: 1_000 }, { label: '5s', ms: 5_000 },
  { label: '30s', ms: 30_000 }, { label: '1m', ms: 60_000 },
  { label: '5m', ms: 300_000 }, { label: '15m', ms: 900_000 },
] as const;

export type ContextMatchMode = 'anything' | 'service' | 'host' | 'attrs';
export interface ContextMatchEntry { field: string; key: string; value: string }

const TOP_MATCH_FIELDS: Record<SearchDomain, string[]> = {
  logs: ['service', 'severity', 'source', 'trace_id', 'span_id'],
  traces: ['service', 'name', 'kind', 'status', 'trace_id', 'span_id', 'parent_span_id', 'duration_us'],
};

export function buildAvailableFields(record: DetailRow, domain: SearchDomain = 'logs'): ContextMatchEntry[] {
  const entries: ContextMatchEntry[] = [];
  for (const field of TOP_MATCH_FIELDS[domain]) {
    const value = record[field];
    if (value !== null && value !== undefined && String(value).length) entries.push({ field, key: field, value: String(value) });
  }
  for (const root of ['attributes', 'resource']) {
    let value: unknown = record[root];
    try { if (typeof value === 'string') value = JSON.parse(value); } catch { continue; }
    const visit = (node: unknown, path: JsonSegment[]) => {
      if (path.length > 16 || entries.length >= 256) return;
      if (node !== null && typeof node === 'object') {
        Object.entries(node).forEach(([key, child]) => visit(child, [...path, Array.isArray(node) ? Number(key) : key]));
      } else if (path.length && node !== null && node !== undefined && String(node).length) {
        entries.push({ field: formatJsonField(root, path), key: path.map(String).join('.'), value: String(node) });
      }
    };
    visit(value, []);
  }
  return entries;
}

export function contextIdentity(row: DetailRow, domain: SearchDomain): string {
  if (domain === 'traces') return JSON.stringify([row.trace_id, row.span_id, timestampUs(row.ts)]);
  return row.id != null ? String(row.id) : JSON.stringify([row.ts, row.service, row.severity, row.body, row.trace_id, row.span_id]);
}

export function contextTimestamp(us: number): string {
  const milliseconds = Math.floor(us / 1000);
  return new Date(milliseconds).toISOString().replace(/\.\d{3}Z$/, '.' + String(((us % 1_000_000) + 1_000_000) % 1_000_000).padStart(6, '0') + 'Z');
}

export interface ContextQueryRequest { sql: string; from: string; to: string; parameters: SearchSqlParameters }

export function buildContextQuery(args: {
  record: DetailRow; windowMs: number; mode: ContextMatchMode; matching: ContextMatchEntry[];
  domain?: SearchDomain; limit?: number; offset?: number;
}): ContextQueryRequest {
  const { record, windowMs, mode, matching, domain = 'logs' } = args;
  if (matching.length > CONTEXT_MAX_MATCHES) throw new Error('Too many context matches');
  const parameters: SearchSqlParameters = { page_limit: (args.limit ?? CONTEXT_LIMIT) + 1, page_offset: args.offset ?? 0 };
  const conditions = ['ts >= $start', 'ts < $end'];
  if (mode === 'service') {
    if (!record.service) throw new Error('Missing service');
    parameters.service = String(record.service);
    conditions.push('service = $service');
  }
  if (mode === 'host') {
    const host = buildAvailableFields(record, domain).find((entry) => entry.field === 'resource["host.name"]');
    if (!host) throw new Error('Missing host.name');
    parameters.host = host.value;
    conditions.push(fieldExpression(host.field, domain, parameters) + ' = $host');
  }
  if (mode === 'attrs') matching.forEach((entry, index) => {
    parameters['m' + index] = entry.value;
    conditions.push(fieldExpression(entry.field, domain, parameters) + ' = $m' + index);
  });
  const ts = timestampUs(record.ts);
  if (!Number.isFinite(ts) || !Number.isFinite(windowMs) || windowMs <= 0) throw new Error('Invalid context time');
  return {
    sql: [
      domain === 'logs'
        ? 'SELECT CAST(id AS VARCHAR) AS id, ts, service, severity, body, trace_id, span_id, source, attributes, resource'
        : 'SELECT ts, service, name, duration_us, status, kind, trace_id, span_id, parent_span_id, attributes, resource, events',
      domain === 'logs' ? 'FROM {{logs}}' : 'FROM {{traces}}',
      'WHERE ' + conditions.join(' AND '),
      domain === 'logs'
        ? 'ORDER BY ts ASC, id ASC NULLS LAST, service ASC, severity ASC, body ASC, trace_id ASC, span_id ASC'
        : 'ORDER BY ts ASC, trace_id ASC, span_id ASC, service ASC, name ASC, duration_us ASC',
      'LIMIT CAST($page_limit AS BIGINT) OFFSET CAST($page_offset AS BIGINT)',
    ].join('\n'),
    from: contextTimestamp(ts - windowMs * 1000),
    to: contextTimestamp(ts + windowMs * 1000),
    parameters,
  };
}
