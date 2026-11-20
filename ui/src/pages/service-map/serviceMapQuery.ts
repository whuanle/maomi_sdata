import type { ExecuteSearchSqlRequest, SearchSqlParameters } from '@/api/types';
import { compileLucene } from '@/pages/search/lucene';
import { bindBarWhere } from '@/pages/charts/barWhere';

export interface MapQuery {
  from: string;
  to: string;
  service: string;
  syntax: 'sql' | 'lucene';
  query: string;
  sampling: number;
}

export const NODE_LIMIT = 100;
export const EDGE_LIMIT = 300;

/** 筛选只确定 Trace 集合；采样后保留窗口内全部父子片段，不能先限制 span 行数. */
export function mapQueryBase(input: MapQuery) {
  if (!Number.isInteger(input.sampling) || input.sampling < 1 || input.sampling > 100) throw new Error('Sampling must be 1–100');
  const width = Date.parse(input.to) - Date.parse(input.from);
  if (!Number.isFinite(width) || width <= 0) throw new Error('Invalid time range');
  const parameters: SearchSqlParameters = { sampling: input.sampling, service: input.service, bucket: Math.max(1, Math.ceil(width / 1000 / 40)) };
  const where = input.syntax === 'lucene' ? compileLucene(input.query, 'traces', parameters).sql : bindBarWhere(input.query, parameters, true);
  const sql = `WITH windowed AS MATERIALIZED (
 SELECT * FROM {{traces}} WHERE ts >= $start AND ts < $end AND trace_id IS NOT NULL AND trace_id <> ''
), matched AS (
 SELECT DISTINCT trace_id FROM windowed
 WHERE ($service = '' OR service = $service) AND (${where || 'TRUE'})
), sampled AS (
 SELECT trace_id FROM matched WHERE hash(trace_id) % 100 < CAST($sampling AS UBIGINT)
), spans AS MATERIALIZED (
 SELECT * EXCLUDE (rank) FROM (
 SELECT w.*, row_number() OVER (PARTITION BY w.trace_id, w.span_id ORDER BY w.ts, w.service, w.parent_span_id NULLS FIRST, w.name, w.duration_us, w.status, w.kind) AS rank
 FROM windowed w INNER JOIN sampled s ON w.trace_id = s.trace_id
 WHERE w.span_id IS NOT NULL AND w.span_id <> ''
 ) WHERE rank = 1
), relations AS MATERIALIZED (
 SELECT c.*, p.service AS parent_service, p.span_id AS found_parent FROM spans c
 LEFT JOIN spans p ON c.trace_id = p.trace_id AND c.parent_span_id = p.span_id
)`;
  return { sql, parameters };
}

export function buildMapQuery(input: MapQuery): ExecuteSearchSqlRequest {
  const { sql, parameters } = mapQueryBase(input);
  return { from: input.from, to: input.to, parameters, rowLimit: 500, sql: `${sql},
nodes AS (
 SELECT service, count(*) AS calls, count(DISTINCT trace_id) AS traces,
 count(*) FILTER (WHERE status = 'error') AS errors, avg(duration_us) AS duration,
 min(ts) AS first_seen, max(ts) AS last_seen FROM spans GROUP BY service
), edges AS (
 SELECT parent_service AS source, service AS target, count(*) AS calls,
 count(DISTINCT trace_id) AS traces, count(*) FILTER (WHERE status = 'error') AS errors,
 avg(duration_us) AS duration, min(ts) AS first_seen, max(ts) AS last_seen
 FROM relations WHERE found_parent IS NOT NULL AND parent_service <> service GROUP BY parent_service, service
), top_nodes AS (SELECT * FROM nodes ORDER BY calls DESC, service LIMIT 100),
top_edges AS (
 SELECT e.* FROM edges e INNER JOIN top_nodes a ON e.source = a.service INNER JOIN top_nodes b ON e.target = b.service
 ORDER BY e.calls DESC, e.source, e.target LIMIT 300
), buckets AS (
 SELECT floor(epoch(ts) / $bucket) * $bucket * 1000 AS bucket_ms, count(DISTINCT trace_id) AS traces FROM spans GROUP BY bucket_ms
)
SELECT 'node' AS type, service AS source, '' AS target, calls, traces, errors, duration, strftime(first_seen, '%Y-%m-%dT%H:%M:%S.%fZ') AS first_seen, strftime(last_seen, '%Y-%m-%dT%H:%M:%S.%fZ') AS last_seen FROM top_nodes
UNION ALL
SELECT 'edge', source, target, calls, traces, errors, duration, strftime(first_seen, '%Y-%m-%dT%H:%M:%S.%fZ'), strftime(last_seen, '%Y-%m-%dT%H:%M:%S.%fZ') FROM top_edges
UNION ALL
SELECT 'bucket', CAST(bucket_ms AS VARCHAR), '', 0, traces, 0, 0, '', '' FROM buckets
UNION ALL
SELECT 'meta', '', '', (SELECT count(*) FROM nodes), (SELECT count(*) FROM sampled),
 (SELECT count(*) FROM relations WHERE parent_span_id IS NOT NULL AND parent_span_id <> '' AND found_parent IS NULL),
 (SELECT count(*) FROM edges), CAST((SELECT count(*) FROM relations WHERE parent_span_id IS NULL OR parent_span_id = '') AS VARCHAR),
 CAST((SELECT count(*) FROM matched) AS VARCHAR)` };
}

export function buildMapExamples(input: MapQuery, selection: { source: string; target?: string }): ExecuteSearchSqlRequest {
  const { sql, parameters } = mapQueryBase(input);
  parameters.source = selection.source;
  parameters.target = selection.target ?? null;
  return { from: input.from, to: input.to, parameters, rowLimit: 21, sql: `${sql}
SELECT trace_id, arg_min(name, ts) AS name, min(ts) AS ts, count(*) AS calls
FROM relations WHERE service = $source AND $target IS NULL
 OR parent_service = $source AND service = $target AND found_parent IS NOT NULL
GROUP BY trace_id ORDER BY ts DESC, trace_id LIMIT 21` };
}
