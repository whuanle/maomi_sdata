import { describe, expect, it } from 'vitest';
import { formatBytesRate, formatDurationUs, formatRate } from '../format';

describe('rate formatting', () => {
  it.each([[0, '0'], [1, '1'], [1.25, '1.25'], [0.0333333333, '0.03'], [0.001, '<0.01'], [NaN, '—'], [Infinity, '—'], [-1, '—']])('formats rate %s', (value, expected) => {
    expect(formatRate(Number(value))).toBe(expected);
  });

  it.each([[0, '0 B/s'], [37.733333333333334, '37.7 B/s'], [19.1, '19.1 B/s'], [100, '100 B/s'], [0.01, '<0.1 B/s'], [1023, '1023 B/s'], [1024, '1 KB/s'], [1536, '1.5 KB/s'], [1024 ** 2, '1 MB/s'], [1024 ** 3, '1 GB/s'], [NaN, '—'], [Infinity, '—'], [-1, '—']])('formats byte rate %s', (value, expected) => {
    expect(formatBytesRate(Number(value))).toBe(expected);
  });
});

describe('formatDurationUs', () => {
  it.each([[0, '0 µs'], [1, '1 µs'], [999, '999 µs'], [1000, '1.0 ms'], [999999, '1.000 s'], [1000000, '1.000 s'], [NaN, '-'], [Infinity, '-'], [-1, '-']])('formats %s', (value, expected) => {
    expect(formatDurationUs(Number(value))).toBe(expected);
  });
});
