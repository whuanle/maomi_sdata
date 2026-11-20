import { describe, expect, it } from 'vitest';
import dayjs from 'dayjs';
import { parseNaturalDate } from '../naturalDate';

/** 固定参照时间：2026-11-17 14:05:01（周二）. */
const NOW = dayjs('2026-11-17T14:05:01');

describe('parseNaturalDate', () => {
  it('关键词：now/今天/昨天/明天（中英文）', () => {
    expect(parseNaturalDate('now', NOW)?.format('YYYY-MM-DD HH:mm:ss')).toBe('2026-11-17 14:05:01');
    expect(parseNaturalDate('现在', NOW)?.format('YYYY-MM-DD HH:mm:ss')).toBe('2026-11-17 14:05:01');
    expect(parseNaturalDate('today', NOW)?.format('YYYY-MM-DD HH:mm:ss')).toBe('2026-11-17 00:00:00');
    expect(parseNaturalDate('昨天', NOW)?.format('YYYY-MM-DD HH:mm:ss')).toBe('2026-11-16 00:00:00');
    expect(parseNaturalDate('yesterday', NOW)?.format('YYYY-MM-DD HH:mm:ss')).toBe('2026-11-16 00:00:00');
    expect(parseNaturalDate('明天', NOW)?.format('YYYY-MM-DD HH:mm:ss')).toBe('2026-11-18 00:00:00');
  });

  it('相对偏移：ago/前/后 与单位别名（中英文）', () => {
    expect(parseNaturalDate('1h ago', NOW)?.valueOf()).toBe(NOW.subtract(1, 'hour').valueOf());
    expect(parseNaturalDate('3 hours ago', NOW)?.valueOf()).toBe(NOW.subtract(3, 'hour').valueOf());
    expect(parseNaturalDate('3小时前', NOW)?.valueOf()).toBe(NOW.subtract(3, 'hour').valueOf());
    expect(parseNaturalDate('30分钟前', NOW)?.valueOf()).toBe(NOW.subtract(30, 'minute').valueOf());
    expect(parseNaturalDate('2d', NOW)?.valueOf()).toBe(NOW.subtract(2, 'day').valueOf());
    expect(parseNaturalDate('7天前', NOW)?.valueOf()).toBe(NOW.subtract(7, 'day').valueOf());
    expect(parseNaturalDate('in 2 hours', NOW)?.valueOf()).toBe(NOW.add(2, 'hour').valueOf());
    expect(parseNaturalDate('1小时后', NOW)?.valueOf()).toBe(NOW.add(1, 'hour').valueOf());
    expect(parseNaturalDate('1周前', NOW)?.valueOf()).toBe(NOW.subtract(1, 'week').valueOf());
  });

  it('当日时刻与标准日期格式', () => {
    expect(parseNaturalDate('09:30', NOW)?.format('YYYY-MM-DD HH:mm')).toBe('2026-11-17 09:30');
    expect(parseNaturalDate('2026-11-10', NOW)?.format('YYYY-MM-DD HH:mm')).toBe('2026-11-10 00:00');
    expect(parseNaturalDate('2026-11-10 08:30', NOW)?.format('YYYY-MM-DD HH:mm')).toBe('2026-11-10 08:30');
    expect(parseNaturalDate('2026-11-10T08:30', NOW)?.format('YYYY-MM-DD HH:mm')).toBe('2026-11-10 08:30');
  });

  it('空输入与无法识别的写法返回 null', () => {
    expect(parseNaturalDate('', NOW)).toBeNull();
    expect(parseNaturalDate('   ', NOW)).toBeNull();
    expect(parseNaturalDate('last monday at 5pm', NOW)).toBeNull();
    expect(parseNaturalDate('abc', NOW)).toBeNull();
    expect(parseNaturalDate('3光年前', NOW)).toBeNull();
  });
});
