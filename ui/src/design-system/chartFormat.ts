import { formatBytes } from '@/utils/format';

/** 图表坐标轴、提示框和数值卡共用的单位格式化. */
export function formatChartValue(value: number | null | undefined, unit?: string): string {
  if (value == null || !Number.isFinite(value)) {
    return '-';
  }

  switch (unit) {
    case 'percent':
      return `${value.toFixed(1)}%`;
    case 'ms':
      return Math.abs(value) >= 1000 ? `${(value / 1000).toFixed(2)}s` : `${value.toFixed(1)}ms`;
    case 'bytes':
      return formatBytes(value);
    case 'ops':
      return `${Math.abs(value) < 10 ? value.toFixed(2) : value.toFixed(1)}/s`;
    default:
      if (Math.abs(value) >= 1_000_000) {
        return `${(value / 1_000_000).toFixed(2)}M`;
      }

      if (Math.abs(value) >= 1000) {
        return `${(value / 1000).toFixed(2)}k`;
      }

      return Math.abs(value) >= 100 ? value.toFixed(0) : value.toFixed(2).replace(/\.?0+$/, '');
  }
}
