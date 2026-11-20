import dayjs, { type Dayjs } from 'dayjs';
import customParseFormat from 'dayjs/plugin/customParseFormat';

// 多格式显式解析（YYYY-MM-DD HH:mm 等）依赖 customParseFormat 插件
dayjs.extend(customParseFormat);

/**
 * 自然语言/宽松时间输入解析（ClickStack 式）：
 * 除 datetime-local 精确格式外，支持 now/今天/昨天/明天、相对偏移（1h ago、3 小时前、2d）、
 * HH:mm（当日）、YYYY-MM-DD、YYYY-MM-DD HH:mm 等常见写法（中英文）。
 * 无法识别时返回 null，由调用方提示。
 */

/** 相对偏移单位（正则别名 → dayjs 单位）. */
const REL_UNITS: Array<[RegExp, dayjs.ManipulateType]> = [
  [/^(s|sec|secs|second|seconds|秒(钟)?)$/, 'second'],
  [/^(m|min|mins|minute|minutes|分(钟)?)$/, 'minute'],
  [/^(h|hr|hrs|hour|hours|小时|时|个小时)$/, 'hour'],
  [/^(d|day|days|天|日)$/, 'day'],
  [/^(w|week|weeks|周|星期)$/, 'week'],
  [/^(mo|month|months|个月|月)$/, 'month'],
];

/** 解析「N 单位」偏移量（数值 + 单位别名）. */
function parseRelAmount(amount: string, unit: string): { amount: number; unit: dayjs.ManipulateType } | null {
  const value = Number(amount);
  if (!Number.isFinite(value)) {
    return null;
  }

  const matched = REL_UNITS.find(([pattern]) => pattern.test(unit));
  return matched ? { amount: value, unit: matched[1] } : null;
}

/**
 * 解析时间输入（自然语言或标准格式）.
 * @param input 用户输入（去除首尾空白）
 * @param now 参照当前时间（默认 dayjs()）
 * @returns 解析成功返回 Dayjs，失败返回 null
 */
export function parseNaturalDate(input: string, now: Dayjs = dayjs()): Dayjs | null {
  const text = input.trim().toLowerCase();
  if (text.length === 0) {
    return null;
  }

  // 关键词：现在 / 今天 / 昨天 / 明天
  if (/^(now|现在|此刻)$/.test(text)) {
    return now;
  }

  if (/^(today|今天|今日)$/.test(text)) {
    return now.startOf('day');
  }

  if (/^(yesterday|昨天|昨日)$/.test(text)) {
    return now.startOf('day').subtract(1, 'day');
  }

  if (/^(tomorrow|明天|明日)$/.test(text)) {
    return now.startOf('day').add(1, 'day');
  }

  // 相对偏移：英文「3 hours ago」/ 中文「3小时前」/「3 天前」/ 省略 ago 的「1h」
  const agoEn = text.match(/^(\d+(?:\.\d+)?)\s*([a-z\u4e00-\u9fff]+)\s*(?:ago|before)$/);
  const agoZh = text.match(/^(\d+(?:\.\d+)?)\s*([a-z\u4e00-\u9fff]+?)前$/);
  const bare = text.match(/^(\d+(?:\.\d+)?)\s*([a-z\u4e00-\u9fff]+)$/);
  for (const match of [agoEn, agoZh, bare]) {
    if (!match) {
      continue;
    }

    const rel = parseRelAmount(match[1], match[2]);
    if (rel) {
      return now.subtract(rel.amount, rel.unit);
    }
  }

  // 未来偏移：「in 3 hours」/「3小时后」
  const inEn = text.match(/^in\s+(\d+(?:\.\d+)?)\s*([a-z\u4e00-\u9fff]+)$/);
  const laterZh = text.match(/^(\d+(?:\.\d+)?)\s*([a-z\u4e00-\u9fff]+?)后$/);
  for (const match of [inEn, laterZh]) {
    if (!match) {
      continue;
    }

    const rel = parseRelAmount(match[1], match[2]);
    if (rel) {
      return now.add(rel.amount, rel.unit);
    }
  }

  // 当日时刻：14:05 / 9:30
  const timeOnly = text.match(/^(\d{1,2}):(\d{2})$/);
  if (timeOnly) {
    const parsed = now.hour(Number(timeOnly[1])).minute(Number(timeOnly[2])).second(0).millisecond(0);
    return parsed.isValid() ? parsed : null;
  }

  // 标准格式回退：YYYY-MM-DD [HH:mm[:ss]] / datetime-local 值
  const standard = dayjs(input.trim(), ['YYYY-MM-DD HH:mm:ss', 'YYYY-MM-DD HH:mm', 'YYYY-MM-DDTHH:mm:ss', 'YYYY-MM-DDTHH:mm', 'YYYY-MM-DD']);
  if (standard.isValid()) {
    return standard;
  }

  return null;
}
