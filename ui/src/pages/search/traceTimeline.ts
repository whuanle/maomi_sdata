import type { TraceRow } from '@/api/traceSearch';

/** ISO 时间保留微秒小数，先减去整毫秒基准再用于图表定位。 */
export function timestampUs(value: unknown): number {
  const text = String(value ?? '');
  const ms = Date.parse(text);
  if (!Number.isFinite(ms)) return Number.NaN;
  const fraction = text.match(/\.(\d+)(?:Z|[+-]\d\d:\d\d)$/)?.[1] ?? '';
  return ms * 1000 + Number(fraction.padEnd(6, '0').slice(3, 6));
}

export interface TimelineRow {
  span: TraceRow;
  depth: number;
  parentId: string | null;
}

export function buildTraceTree(spans: TraceRow[]): { rows: TimelineRow[]; incomplete: boolean } {
  const sorted = [...spans].sort((a, b) => timestampUs(a.ts) - timestampUs(b.ts) || String(a.span_id).localeCompare(String(b.span_id)));
  const byId = new Map(sorted.map((span) => [String(span.span_id), span]));
  const children = new Map<string, TraceRow[]>();
  const roots: TraceRow[] = [];
  let incomplete = false;
  for (const span of sorted) {
    const parent = String(span.parent_span_id ?? '');
    if (!parent || /^0+$/.test(parent)) roots.push(span);
    else if (!byId.has(parent) || parent === span.span_id) {
      roots.push(span);
      incomplete = true;
    } else {
      const list = children.get(parent) ?? [];
      list.push(span);
      children.set(parent, list);
    }
  }
  const rows: TimelineRow[] = [];
  const visited = new Set<string>();
  const visit = (root: TraceRow) => {
    const stack = [{ span: root, depth: 0, parentId: null as string | null }];
    while (stack.length) {
      const item = stack.pop()!;
      const id = String(item.span.span_id);
      if (visited.has(id)) { incomplete = true; continue; }
      visited.add(id);
      rows.push(item);
      for (const child of [...(children.get(id) ?? [])].reverse()) {
        stack.push({ span: child, depth: item.depth + 1, parentId: id });
      }
    }
  };
  roots.forEach(visit);
  for (const span of sorted) {
    if (!visited.has(String(span.span_id))) { incomplete = true; visit(span); }
  }
  return { rows, incomplete };
}

export function spanEvents(value: unknown): { name: string; timestamp: string; attributes?: unknown }[] {
  try {
    const items: unknown = typeof value === 'string' ? JSON.parse(value) : value;
    if (!Array.isArray(items)) return [];
    return items.filter((item) => item && typeof item === 'object').map((item: Record<string, unknown>) => ({
      name: String(item.name ?? ''),
      timestamp: typeof item.timestamp === 'string' ? item.timestamp : nanoTimestamp(item.timeUnixNano),
      attributes: normalizeEventAttributes(item.attributes),
    }));
  } catch { return []; }
}

function normalizeEventAttributes(value: unknown): unknown {
  if (!Array.isArray(value)) return value;
  return Object.fromEntries(value.filter((item) => item && typeof item.key === 'string').map((item) => [item.key, eventValue(item.value)]));
}

function eventValue(value: unknown): unknown {
  if (!value || typeof value !== 'object') return value;
  const item = value as Record<string, unknown>;
  for (const key of ['stringValue', 'boolValue', 'intValue', 'doubleValue', 'bytesValue']) {
    if (key in item) return item[key];
  }
  const array = item.arrayValue as { values?: unknown[] } | undefined;
  if (array) return (array.values ?? []).map(eventValue);
  const list = item.kvlistValue as { values?: unknown[] } | undefined;
  return list ? normalizeEventAttributes(list.values ?? []) : null;
}

function nanoTimestamp(value: unknown): string {
  try {
    const ns = BigInt(String(value));
    return new Date(Number(ns / 1000000n)).toISOString().replace(/\.\d{3}Z$/, '.' + String(ns % 1000000000n).padStart(9, '0').slice(0, 6) + 'Z');
  } catch { return ''; }
}
