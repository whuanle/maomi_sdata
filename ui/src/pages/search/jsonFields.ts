import { SEARCH_FIELDS } from '@/api/search';
import type { SearchSqlParameters } from '@/api/types';
import type { ChartSource as SearchDomain } from '@/api/chartTypes';

export type JsonSegment = string | number;
export interface JsonField { root: string; segments: JsonSegment[] }

export function jsonRoots(domain: SearchDomain): string[] {
  return [...SEARCH_FIELDS[domain].filter((field) => field.kind === 'json').map((field) => field.field), ...(domain === 'logs' ? ['body'] : [])];
}

export function formatJsonField(root: string, segments: JsonSegment[]): string {
  return root + segments.map((segment) => `[${JSON.stringify(segment)}]`).join('');
}

export function parseJsonField(field: string, domain: SearchDomain): JsonField | null {
  const root = field.match(/^\w+/)?.[0];
  if (!root || !jsonRoots(domain).includes(root) || field === root) return null;
  const suffix = field.slice(root.length);
  if (suffix.startsWith('.')) {
    const key = suffix.slice(1);
    return key && /^[^"'\\]+$/.test(key) ? { root, segments: [key] } : null;
  }
  const segments: JsonSegment[] = [];
  let position = 0;
  while (position < suffix.length) {
    const match = suffix.slice(position).match(/^\[((?:"(?:[^"\\]|\\.)*")|(?:0|[1-9]\d*))\]/);
    if (!match) return null;
    let segment: JsonSegment;
    try { segment = JSON.parse(match[1]) as JsonSegment; } catch { return null; }
    if (typeof segment === 'number' && !Number.isSafeInteger(segment)) return null;
    segments.push(segment);
    position += match[0].length;
  }
  return segments.length > 0 && segments.length <= 16 ? { root, segments } : null;
}

export function duckJsonPath(segments: JsonSegment[]): string {
  return '$' + segments.map((segment) => typeof segment === 'number' ? `[${segment}]` : `.${JSON.stringify(segment)}`).join('');
}

/** 路径绑定（同名同值复用，Load more 重复构建 SQL 时参数字典保持稳定）. */
export function bindJsonPath(parameters: SearchSqlParameters, path: string): string {
  const existing = Object.entries(parameters).find(([, value]) => value === path);
  if (existing) {
    return `$${existing[0]}`;
  }

  let index = Object.keys(parameters).length;
  while (Object.prototype.hasOwnProperty.call(parameters, `jp${index}`)) index++;
  const name = `jp${index}`;
  parameters[name] = path;
  return `$${name}`;
}

export function jsonFieldExpression(field: JsonField, parameters: SearchSqlParameters): string {
  const path = bindJsonPath(parameters, duckJsonPath(field.segments));
  return `json_extract_string(CASE WHEN json_valid(${field.root}) THEN ${field.root} ELSE NULL END, ${path})`;
}

/** 字段列表中的逗号可能属于 JSON key，不能直接 split(','). */
export function splitFieldList(value: string): string[] {
  const parts: string[] = [];
  let start = 0;
  let quoted = false;
  let escaped = false;
  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    if (escaped) { escaped = false; continue; }
    if (quoted && ch === '\\') { escaped = true; continue; }
    if (ch === '"') quoted = !quoted;
    if (ch === ',' && !quoted) { parts.push(value.slice(start, i).trim()); start = i + 1; }
  }
  parts.push(value.slice(start).trim());
  return parts.filter(Boolean);
}

export function quoteFieldAlias(field: string): string {
  return '"' + field.replace(/"/g, '""') + '"';
}
