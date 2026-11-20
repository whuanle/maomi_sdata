import { describe, expect, it } from 'vitest';
import {
  bindJsonPath,
  duckJsonPath,
  formatJsonField,
  jsonFieldExpression,
  jsonRoots,
  parseJsonField,
  quoteFieldAlias,
  splitFieldList,
} from '../jsonFields';

describe('jsonFields', () => {
  it('jsonRoots：json 列 + logs 的 body，traces 不含 body', () => {
    expect(jsonRoots('logs')).toEqual(['attributes', 'resource', 'body']);
    expect(jsonRoots('traces')).toEqual(['attributes', 'resource', 'events']);
  });

  it('formatJsonField：对象 key 双引号包裹、数组索引裸数字', () => {
    expect(formatJsonField('attributes', ['http', 'status_code'])).toBe('attributes["http"]["status_code"]');
    expect(formatJsonField('body', ['items', 0, 'name'])).toBe('body["items"][0]["name"]');
    expect(formatJsonField('attributes', ['my key'])).toBe('attributes["my key"]');
  });

  it('parseJsonField：括号路径往返解析', () => {
    expect(parseJsonField('attributes["http"]["status_code"]', 'logs')).toEqual({ root: 'attributes', segments: ['http', 'status_code'] });
    expect(parseJsonField('body["items"][0]["name"]', 'logs')).toEqual({ root: 'body', segments: ['items', 0, 'name'] });
    // 点号 legacy：单层字面 key（兼容旧语法 attributes.http）
    expect(parseJsonField('attributes.http', 'logs')).toEqual({ root: 'attributes', segments: ['http'] });
    expect(parseJsonField('attributes.http.status_code', 'logs')).toEqual({ root: 'attributes', segments: ['http.status_code'] });
  });

  it('parseJsonField：拒绝非法路径', () => {
    expect(parseJsonField('attributes', 'logs')).toBeNull(); // 裸根列不是子字段
    expect(parseJsonField('attributes["unterminated]', 'logs')).toBeNull();
    expect(parseJsonField('attributes["a"trailing]', 'logs')).toBeNull(); // 残缺段
    expect(parseJsonField('attributes.环"境', 'logs')).toBeNull(); // key 含引号
    expect(parseJsonField('service.x', 'logs')).toBeNull(); // 非 JSON 根
    expect(parseJsonField('body["a"]'.repeat(20), 'logs')).toBeNull(); // 深度上限
  });

  it('duckJsonPath：key 段点号连接（JSON 转义），索引段方括号', () => {
    expect(duckJsonPath(['http', 'status_code'])).toBe('$."http"."status_code"');
    expect(duckJsonPath(['items', 0])).toBe('$."items"[0]');
    expect(duckJsonPath(['a"b'])).toBe('$."a\\"b"');
  });

  it('jsonFieldExpression：json_valid 防护 + 路径参数绑定', () => {
    const params = {};
    expect(jsonFieldExpression({ root: 'body', segments: ['k'] }, params))
      .toBe('json_extract_string(CASE WHEN json_valid(body) THEN body ELSE NULL END, $jp0)');
    expect(params).toEqual({ jp0: '$."k"' });
  });

  it('bindJsonPath：同路径复用绑定名（Load more 重建 SQL 时字典稳定），与既有 $key 编号错开但保持唯一', () => {
    const params = {};
    expect(bindJsonPath(params, '$."a"')).toBe('$jp0');
    expect(bindJsonPath(params, '$."a"')).toBe('$jp0');
    expect(bindJsonPath(params, '$."b"')).toBe('$jp1');
    expect(params).toEqual({ jp0: '$."a"', jp1: '$."b"' });

    const mixed = { key: '%x%' };
    expect(bindJsonPath(mixed, '$."a"')).toBe('$jp1');
    expect(bindJsonPath(mixed, '$."a"')).toBe('$jp1');
  });

  it('splitFieldList：引号内逗号不拆分', () => {
    expect(splitFieldList('ts, service')).toEqual(['ts', 'service']);
    expect(splitFieldList('ts, attributes["a,b"], body["c"]')).toEqual(['ts', 'attributes["a,b"]', 'body["c"]']);
    expect(splitFieldList('ts')).toEqual(['ts']);
    expect(splitFieldList('')).toEqual([]);
  });

  it('quoteFieldAlias：双引号 SQL 转义', () => {
    expect(quoteFieldAlias('attributes["x"]')).toBe('"attributes[""x""]"');
  });
});
