import type { ChartValueFormat } from '@/api/chartTypes';

export const DEFAULT_VALUE_FORMAT: ChartValueFormat = { output: 'number', decimals: 2, thousands: true, compact: false };

/** 轴刻度自动缩写；tooltip 仍使用完整的用户数值格式. */
export function formatBarAxisValue(value: number, format = DEFAULT_VALUE_FORMAT): string {
  const axisFormat = { ...format, compact: ['number', 'currency', 'throughput'].includes(format.output) || format.compact };
  if (['number', 'currency', 'throughput', 'percentage'].includes(format.output)) {
    const numeric = format.output === 'percentage' ? value * 100 : value;
    const label = new Intl.NumberFormat('en-US', {
      maximumFractionDigits: Math.max(0, Math.min(10, Math.trunc(format.decimals))),
      useGrouping: format.thousands,
      notation: axisFormat.compact ? 'compact' : 'standard',
    }).format(numeric);
    return format.output === 'currency' ? `$${label}` : format.output === 'percentage' ? `${label}%` : format.output === 'throughput' ? `${label}/s` : label;
  }
  return formatBarValue(value, axisFormat);
}

/** 分类图数值格式；单位默认秒、字节和每秒次数. */
export function formatBarValue(value: number | null | undefined, format = DEFAULT_VALUE_FORMAT): string {
  if (value == null || !Number.isFinite(value)) return '-';
  const decimals = Math.max(0, Math.min(10, Math.trunc(format.decimals)));
  const number = (n: number) => new Intl.NumberFormat('en-US', {
    minimumFractionDigits: decimals, maximumFractionDigits: decimals,
    useGrouping: format.thousands, notation: format.compact ? 'compact' : 'standard',
  }).format(n);
  switch (format.output) {
    case 'currency': return `$${number(value)}`;
    case 'percentage': return `${number(value * 100)}%`;
    case 'throughput': return `${number(value)}/s`;
    case 'time': {
      const seconds = Math.floor(Math.abs(value));
      return `${value < 0 ? '-' : ''}${Math.floor(seconds / 3600)}:${String(Math.floor(seconds / 60) % 60).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
    }
    case 'duration': {
      const units: [number, string][] = [[86400, 'd'], [3600, 'h'], [60, 'min'], [1, 's'], [0.001, 'ms'], [0.000001, 'µs']];
      const [scale, unit] = units.find(([scale]) => Math.abs(value) >= scale) ?? units[units.length - 1];
      return `${number(value / scale)} ${unit}`;
    }
    case 'data':
    case 'data_rate': {
      const index = Math.min(5, Math.max(0, Math.floor(Math.log2(Math.abs(value) || 1) / 10)));
      return `${number(value / 1024 ** index)} ${['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'][index]}${format.output === 'data_rate' ? '/s' : ''}`;
    }
    default: return number(value);
  }
}
