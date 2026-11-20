import type { ChartSource } from '@/api/chartTypes';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import { jsonFieldExpression, parseJsonField } from '@/pages/search/jsonFields';
import { bindChartParameter } from './formula';

export const PATTERN_LIMIT = 200;
export const PATTERN_BUCKETS = 30;
export const PATTERN_DEFAULTS: Record<ChartSource, string> = {
  logs: 'body', traces: 'name', metrics: 'name', sessions: 'domain', iot: 'str_value',
};
const FIELDS: Record<ChartSource, string[]> = {
  logs: ['ts', 'id', 'service', 'severity', 'body', 'trace_id', 'span_id', 'source', 'attributes', 'resource'],
  traces: ['ts', 'service', 'name', 'duration_us', 'status', 'trace_id', 'span_id', 'parent_span_id', 'attributes', 'resource'],
  metrics: ['ts', 'name', 'value', 'labels'],
  sessions: ['ts', 'project_key', 'session_id', 'user_agent', 'domain'],
  iot: ['ts', 'device_key', 'signal', 'num_value', 'str_value', 'quality'],
};
export class PatternExpressionError extends Error {}

/** 只接受白名单字段、绑定的 JSON 路径与三个字符串函数，不接受任意 SQL. */
export function patternExpression(input: string, domain: ChartSource, parameters: SearchSqlParameters, depth = 0): string {
  const value = input.trim() || PATTERN_DEFAULTS[domain];
  if (depth > 4 || value.length > 512) throw new PatternExpressionError();
  const fn = value.match(/^(lower|upper|trim)\((.+)\)$/i);
  if (fn) {
    const inner = patternExpression(fn[2], domain, parameters, depth + 1);
    switch (fn[1].toLowerCase()) {
      case 'lower': return `lower(${inner})`;
      case 'upper': return `upper(${inner})`;
      case 'trim': return `trim(${inner})`;
    }
  }
  if (FIELDS[domain].includes(value)) return '"' + value + '"';
  if (domain === 'logs' || domain === 'traces') {
    const json = parseJsonField(value, domain);
    if (json) return jsonFieldExpression(json, parameters);
  }
  throw new PatternExpressionError();
}

const MASKS = [
  'https?://[^\\s]+',
  '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}',
  '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}',
  '\\b[0-9]{1,3}(\\.[0-9]{1,3}){3}\\b',
  '\\b[0-9a-fA-F]{16,}\\b',
  '[0-9]+(\\.[0-9]+)?',
];
export interface PatternQueryInput {
  domain: ChartSource;
  expression?: string;
  where?: string;
  parameters: SearchSqlParameters;
}
export function buildPatternSql({ domain, expression = '', where = '', parameters }: PatternQueryInput): string {
  const bind = (value: string | number) => bindChartParameter(parameters, value);
  const text = `CAST(${patternExpression(expression, domain, parameters)} AS VARCHAR)`;
  const marker = bind('<*>');
  let normalized = 'raw_text';
  for (const mask of MASKS) normalized = `regexp_replace(${normalized}, ${bind(mask)}, ${marker}, 'g')`;
  normalized = `trim(regexp_replace(${normalized}, ${bind('\\s+')}, ${bind(' ')}, 'g'))`;
  const level = domain === 'logs' ? "lower(coalesce(severity, ''))" : domain === 'traces' ? "lower(coalesce(status, ''))" : bind('');
  const sample = `to_json(struct_pack(${FIELDS[domain].map((field) => `${field} := "${field}"`).join(', ')}))`;
  return `WITH source_events AS (
  SELECT ts, ${text} AS raw_text, ${level} AS level, ${sample} AS sample
  FROM {{${domain}}}
  WHERE ts >= $start AND ts < $end${where.trim() ? ` AND (${where})` : ''}
), normalized AS (
  SELECT ts, ${normalized} AS pattern, level, sample
  FROM source_events WHERE raw_text IS NOT NULL AND length(trim(raw_text)) > 0
), bucketed AS (
  SELECT *, least(${PATTERN_BUCKETS - 1}, greatest(0, CAST(floor(epoch(ts - $start) / nullif(epoch($end - $start), 0) * ${PATTERN_BUCKETS}) AS INTEGER))) AS bucket
  FROM normalized
), totals AS (
  SELECT pattern, level, count(*) AS count, arg_max(sample, ts) AS sample
  FROM bucketed GROUP BY pattern, level
), top_patterns AS (
  SELECT *, count(*) OVER () AS total_patterns FROM totals
  ORDER BY count DESC, pattern, level LIMIT ${PATTERN_LIMIT}
), bins AS (
  SELECT b.pattern, b.level, b.bucket, count(*) AS count
  FROM bucketed b INNER JOIN top_patterns p ON b.pattern = p.pattern AND b.level = p.level
  GROUP BY b.pattern, b.level, b.bucket
), trends AS (
  SELECT pattern, level, to_json(list(struct_pack(bucket := bucket, count := count) ORDER BY bucket)) AS trend
  FROM bins GROUP BY pattern, level
)
SELECT p.pattern, p.level, p.count, p.sample, p.total_patterns, t.trend
FROM top_patterns p INNER JOIN trends t ON p.pattern = t.pattern AND p.level = t.level
ORDER BY p.count DESC, p.pattern, p.level`;
}
export interface PatternRow {
  key: string;
  pattern: string;
  level: string;
  count: number;
  trend: number[];
  sample: Record<string, unknown> | null;
}
export function patternRows(result: AnalyticsQueryResponse | null): PatternRow[] {
  if (!result) return [];
  const index = (name: string) => result.columns.indexOf(name);
  return result.rows.map((cells, i) => {
    const trend = Array<number>(PATTERN_BUCKETS).fill(0);
    let sample: Record<string, unknown> | null = null;
    try {
      const raw = cells[index('sample')];
      const parsed: unknown = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) sample = parsed as Record<string, unknown>;
    } catch { /* 无效代表样本不影响统计展示. */ }
    try {
      const raw = cells[index('trend')];
      const bins: unknown = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (Array.isArray(bins)) for (const bin of bins) {
        if (Number.isInteger(bin?.bucket) && bin.bucket >= 0 && bin.bucket < PATTERN_BUCKETS && Number.isFinite(Number(bin.count))) trend[bin.bucket] = Number(bin.count);
      }
    } catch { /* 缺失趋势保持空桶. */ }
    return { key: String(i), pattern: String(cells[index('pattern')] ?? ''), level: String(cells[index('level')] ?? ''),
      count: Number(cells[index('count')] ?? 0), trend, sample };
  });
}
export function patternTotal(result: AnalyticsQueryResponse | null): number {
  return Number(result?.rows[0]?.[result.columns.indexOf('total_patterns')] ?? 0);
}
export function patternsCsv(rows: PatternRow[]): string {
  const cell = (value: string | number) => {
    const text = String(value);
    const safe = /^[=+\-@\t\r]/.test(text) ? "'" + text : text;
    return '"' + safe.replace(/"/g, '""') + '"';
  };
  return [['Count', 'Level', 'Pattern'], ...rows.map((row) => [row.count, row.level, row.pattern])]
    .map((row) => row.map(cell).join(',')).join('\r\n');
}
