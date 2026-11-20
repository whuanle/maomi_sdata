import type { PieDisplaySettings } from '@/api/chartTypes';
import { chartColors } from '@/design-system/theme';
import { DEFAULT_VALUE_FORMAT } from './barFormat';

export const DEFAULT_PIE_DISPLAY: PieDisplaySettings = { format: { ...DEFAULT_VALUE_FORMAT } };

/** 按完整分类元组去重；展示名冲突不能合并不同分类。 */
export function pieData(result: { columns: string[]; rows: unknown[][] }, fallback: string, settings?: PieDisplaySettings) {
  const valueIndex = result.columns.indexOf('value');
  const dimensions = result.columns.map((name, index) => ({ name, index }))
    .filter(({ name }) => name !== 'value' && name !== 'ts_bucket' && !name.startsWith('__'));
  const idIndex = result.columns.indexOf('__series_id');
  const grouped = new Map<string, { name: string; value: number; key: string }>();
  let invalid = false;
  for (const row of result.rows) {
    const raw = row[valueIndex];
    const value = typeof raw === 'number' || (typeof raw === 'string' && raw.trim()) ? Number(raw) : NaN;
    if (!Number.isFinite(value)) continue;
    if (value < 0) { invalid = true; continue; }
    const tuple = dimensions.map(({ index }) => row[index]);
    const key = JSON.stringify([idIndex >= 0 ? row[idIndex] : null, ...tuple]);
    const current = grouped.get(key);
    const sum = (current?.value ?? 0) + value;
    if (!Number.isFinite(sum)) { invalid = true; continue; }
    grouped.set(key, { key, name: tuple.map((item) => item == null ? '∅' : String(item)).join(' / ') || fallback, value: sum });
  }
  const names = new Map<string, number>();
  const entries = [...grouped.values()].sort((a, b) => b.value - a.value || a.key.localeCompare(b.key));
  const used = new Set<string>();
  const data = entries.map((item) => {
    const count = (names.get(item.name) ?? 0) + 1;
    names.set(item.name, count);
    let name = count === 1 ? item.name : `${item.name} (${count})`;
    while (used.has(name)) name += ' ·';
    used.add(name);
    let hash = 0;
    for (const char of item.key) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
    return { name, value: item.value, itemStyle: { color: chartColors[hash % chartColors.length] } };
  });
  const limit = settings?.seriesLimit;
  const bounded = Number.isInteger(limit) && limit! > 0 ? Math.min(10000, limit!) : undefined;
  return { data: bounded ? data.slice(0, bounded) : data, invalid, limited: bounded !== undefined && data.length > bounded };
}
