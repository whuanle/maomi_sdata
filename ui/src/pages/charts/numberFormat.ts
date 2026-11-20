import type { NumberColor, NumberDisplaySettings, NumberFormat } from './numberTypes';
import { DEFAULT_NUMBER_SETTINGS, normalizeNumberFormat } from './numberTypes';

export function numberValue(value: unknown): number | null {
  if (typeof value !== 'number' && typeof value !== 'string' && typeof value !== 'bigint') return null;
  if (typeof value === 'string' && !/^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?$/i.test(value.trim())) return null;
  const numeric = Number(value);
  return Number.isFinite(numeric) ? numeric : null;
}

/** 整数文本不经 Number，避免 DuckDB 大整数在展示时丢失精度. */
export function formatNumberValue(value: unknown, format: NumberFormat = DEFAULT_NUMBER_SETTINGS.format): string {
  format = normalizeNumberFormat(format);
  const numeric = numberValue(value);
  if (numeric === null) return '-';
  const decimals = Number.isFinite(format.decimals) ? Math.max(0, Math.min(10, Math.trunc(format.decimals))) : 0;
  const options: Intl.NumberFormatOptions = {
    minimumFractionDigits: decimals, maximumFractionDigits: decimals,
    useGrouping: format.thousands, notation: format.compact ? 'compact' : 'standard',
  };
  const number = (n: number | bigint) => new Intl.NumberFormat('en-US', options).format(n);
  if (format.output === 'number' && !format.compact && (typeof value === 'bigint' || typeof value === 'string' && /^[+-]?\d+$/.test(value.trim()))) {
    return number(BigInt(value));
  }
  switch (format.output) {
    case 'currency': return new Intl.NumberFormat('en-US', { ...options, style: 'currency', currency: ['USD', 'EUR', 'CNY', 'JPY', 'GBP'].includes(format.unit ?? '') ? format.unit : 'USD' }).format(numeric);
    case 'percentage': return `${number(numeric * (format.unit === 'percent' ? 1 : 100))}%`;
    case 'throughput': return `${number(numeric)}/${format.unit === 'minute' ? 'min' : format.unit === 'hour' ? 'h' : 's'}`;
    case 'time': {
      const seconds = Math.floor(Math.abs(numeric));
      return `${numeric < 0 ? '-' : ''}${Math.floor(seconds / 3600)}:${String(Math.floor(seconds / 60) % 60).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
    }
    case 'duration': {
      const inputScale = ({ s: 1, ms: 0.001, us: 0.000001, ns: 0.000000001 } as Record<string, number>)[format.unit ?? 's'] ?? 1;
      const seconds = numeric * inputScale;
      const units: [number, string][] = [[86400, 'd'], [3600, 'h'], [60, 'min'], [1, 's'], [0.001, 'ms'], [0.000001, 'µs'], [0.000000001, 'ns']];
      const [scale, unit] = seconds === 0 ? units[3] : units.find(([scale]) => Math.abs(seconds) >= scale) ?? units[6];
      return `${number(seconds / scale)} ${unit}`;
    }
    case 'data':
    case 'data_rate': {
      const base = format.unit === 'decimal' ? 1000 : 1024;
      const index = Math.min(5, Math.max(0, Math.floor(Math.log(Math.abs(numeric) || 1) / Math.log(base))));
      const units = base === 1000 ? ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] : ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
      return `${number(numeric / base ** index)} ${units[index]}${format.output === 'data_rate' ? '/s' : ''}`;
    }
    default: return number(numeric);
  }
}

export function numberColor(value: unknown, settings: NumberDisplaySettings): NumberColor {
  const n = numberValue(value);
  let color = settings.color;
  if (n !== null) {
    for (const rule of settings.rules) {
      if (Number.isFinite(rule.value) && ({
        gt: n > rule.value, gte: n >= rule.value, lt: n < rule.value, lte: n <= rule.value, eq: n === rule.value,
      })[rule.operator]) color = rule.color;
    }
  }
  return color;
}
