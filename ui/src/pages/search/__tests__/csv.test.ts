import { describe, expect, it } from 'vitest';
import { toCsv } from '../csv';

describe('toCsv', () => {
  it('基础序列化：首行列名 + CRLF 行尾', () => {
    expect(toCsv(['ts', 'severity'], [['2026-11-16T10:00:01Z', 'ERROR'], ['2026-11-16T10:00:02Z', 'WARN']]))
      .toBe('ts,severity\r\n2026-11-16T10:00:01Z,ERROR\r\n2026-11-16T10:00:02Z,WARN');
  });

  it('含逗号/引号/换行的值加引号并转义内部引号', () => {
    expect(toCsv(['body'], [['a,b'], ['say "hi"'], ['line1\nline2']]))
      .toBe('body\r\n"a,b"\r\n"say ""hi"""\r\n"line1\nline2"');
  });

  it('null/undefined → 空单元格，对象 JSON 序列化', () => {
    expect(toCsv(['a', 'b'], [[null, undefined], [{ k: 1 }]])).toBe('a,b\r\n,\r\n"{""k"":1}"');
  });
});
