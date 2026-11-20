import type { NumberColor, NumberDisplaySettings, NumberFormat } from '@/api/chartTypes';
export type { NumberColor, NumberDisplaySettings, NumberFormat, NumberTrendQuery } from '@/api/chartTypes';

export interface NumberResult {
  columns: string[];
  rows: unknown[][];
  truncated?: boolean;
}
export const DEFAULT_NUMBER_SETTINGS: NumberDisplaySettings = {
  format: { output: 'number', decimals: 0, thousands: false, compact: false },
  color: 'default', rules: [], background: 'none',
};
export const NUMBER_COLOR_CLASSES: Record<NumberColor, string> = {
  default: 'text-fg', primary: 'text-primary', accent: 'text-accent',
  success: 'text-success', warning: 'text-warning', error: 'text-error',
};

const colors = Object.keys(NUMBER_COLOR_CLASSES);
const outputs = ['number', 'currency', 'percentage', 'duration', 'time', 'data', 'data_rate', 'throughput'];
export function normalizeNumberFormat(value?: Partial<NumberFormat> | null): NumberFormat {
  return {
    output: outputs.includes(value?.output ?? '') ? value!.output! : 'number',
    decimals: Number.isFinite(value?.decimals) ? Math.max(0, Math.min(10, Math.trunc(value!.decimals!))) : 0,
    thousands: value?.thousands === true, compact: value?.compact === true,
    unit: typeof value?.unit === 'string' ? value.unit : undefined,
  };
}

/** 保存配置按旧版本缺省值兼容，非法规则不参与计算. */
export function normalizeNumberSettings(value?: Partial<NumberDisplaySettings> | null): NumberDisplaySettings {
  return {
    format: normalizeNumberFormat(value?.format),
    color: colors.includes(value?.color ?? '') ? value!.color! : 'default',
    background: ['none', 'line', 'area', 'bar'].includes(value?.background ?? '') ? value!.background! : 'none',
    backgroundColor: colors.includes(value?.backgroundColor ?? '') ? value!.backgroundColor : undefined,
    rules: Array.isArray(value?.rules) ? value.rules.filter((rule) => rule && ['gt', 'gte', 'lt', 'lte', 'eq'].includes(rule.operator) && Number.isFinite(rule.value) && colors.includes(rule.color)).slice(0, 12) : [],
  };
}
