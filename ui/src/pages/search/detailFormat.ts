import { formatDateTimeMs, formatDurationUs } from '@/utils/format';

/** severity/status 纯文字语义色（HyperDX 式着色文本，非 Tag 药丸）. */
export const SEVERITY_TEXT_CLS: Record<string, string> = {
  FATAL: 'text-error font-semibold',
  ERROR: 'text-error',
  WARN: 'text-warning',
  WARNING: 'text-warning',
  INFO: 'text-muted',
  DEBUG: 'text-info',
  TRACE: 'text-faint',
  error: 'text-error',
  ok: 'text-muted',
  unset: 'text-faint',
};

/** JSON 字段美化（解析失败按原文展示）. */
export function prettyJson(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '-';
  }

  if (typeof value !== 'string') {
    return JSON.stringify(value, null, 2);
  }

  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

/** 明细值渲染：时间/耗时格式化、JSON 美化、其余原样. */
export function renderDetailValue(field: string, value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '-';
  }

  if (field === 'ts') {
    return formatDateTimeMs(String(value));
  }

  if (field === 'duration_us') {
    return formatDurationUs(Number(value));
  }

  return prettyJson(value);
}
