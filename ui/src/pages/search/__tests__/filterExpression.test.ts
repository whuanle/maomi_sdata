import { describe, expect, it } from 'vitest';
import { editSearchExpression, filterExpression, searchExpression } from '../filterExpression';
import { compileLucene } from '../lucene';
import { effectiveSearch } from '../effectiveSearch';
import type { SearchFilter } from '../filters';

const service: SearchFilter = { id: 1, field: 'service', op: 'in', value: '', values: ['api', 'worker'] };

describe('filterExpression', () => {
  it('多值 OR 与手写 OR 分组，取消筛选不残留文本', () => {
    expect(searchExpression('timeout OR failed', [service])).toBe('(timeout OR failed) (service:"api" OR service:"worker")');
    expect(searchExpression('timeout OR failed', [])).toBe('timeout OR failed');
    const edited = editSearchExpression('(timeout OR failed) (service:"api" OR service:"worker")', [service]);
    expect(edited).toEqual({ where: 'timeout OR failed', filters: [service], denoise: false });
  });

  it('手改自动条件后移除隐藏过滤；任意 OR 不反向提取', () => {
    expect(editSearchExpression('(timeout) service:"new"', [service])).toEqual({
      where: '(timeout) service:"new"', filters: [], denoise: false,
    });
    expect(editSearchExpression('timeout OR (service:"api" OR service:"worker")', [service]).filters).toEqual([]);
    expect(editSearchExpression('', [service])).toEqual({ where: '', filters: [], denoise: false });
    expect(editSearchExpression('NOT (service:"api" OR service:"worker")', [service]).filters).toEqual([]);
  });

  it('只保留未改写的子句，允许在自动条件前后追加草稿', () => {
    const excluded: SearchFilter = { id: 2, field: 'severity', op: 'neq', value: 'DEBUG' };
    const edited = editSearchExpression('timeout (service:"api" OR service:"worker") -severity:"TRACE" error', [service, excluded]);
    expect(edited.filters).toEqual([service]);
    expect(edited.where).toBe('timeout -severity:"TRACE" error');
    expect(editSearchExpression('(timeout', [service]).where).toBe('(timeout');
    expect(editSearchExpression(' AND ', []).where).toBe(' AND ');
    expect(editSearchExpression('service:"api" AND', [{ ...service, values: ['api'] }]).where).toBe('service:"api" AND');
  });

  it('降噪同步展示与移除', () => {
    const text = searchExpression('timeout', [], true);
    expect(text).toBe('(timeout) -(severity:"DEBUG" OR severity:"TRACE")');
    expect(editSearchExpression(text, [], true)).toEqual({ where: 'timeout', filters: [], denoise: true });
    expect(editSearchExpression('timeout', [], true).denoise).toBe(false);
  });

  it.each(['', 'a"b\\c', '>api', 'OR', 'a(b):*', 'line\nbreak'])('引用值按字面量编译：%j', (value) => {
    const compiled = compileLucene(filterExpression({ id: 1, field: 'service', op: 'eq', value }), 'logs');
    expect(compiled.sql).toBe('service = $p0');
    expect(compiled.params).toEqual({ p0: value });
  });

  it('JSON 特殊 key、存在性和数值比较', () => {
    const field = 'attributes["a:b (c)"]';
    const expression = filterExpression({ id: 1, field, op: 'exists', value: '' });
    expect(compileLucene(expression, 'logs').sql).toContain('IS NOT NULL');
    expect(compileLucene('-' + expression, 'logs').sql).toContain('NOT (');
    expect(compileLucene(filterExpression({ id: 2, field: 'duration_us', op: 'gte', value: '1250' }), 'traces').params).toEqual({ p0: 1250 });
  });

  it('prefix、contains 和根 span 均可独立编译', () => {
    expect(compileLucene(filterExpression({ id: 1, field: 'service', op: 'prefix', value: 'a*' }), 'logs'))
      .toEqual({ sql: 'starts_with(service, $p0)', params: { p0: 'a*' } });
    expect(compileLucene(filterExpression({ id: 1, field: 'service', op: 'contains', value: 'a%b' }), 'logs').params)
      .toEqual({ p0: '%a\\%b%' });
    const root = compileLucene(filterExpression({ id: 1, field: 'parent_span_id', op: 'root', value: '' }), 'traces');
    expect(root.sql).toContain('IS NOT NULL');
    expect(root.params).toEqual({ p0: '', p1: '0000000000000000' });
  });

  it('完整展示与独立存储在执行时均不重复应用筛选', () => {
    const filters = [service];
    const text = searchExpression('timeout OR failed', filters);
    const split = effectiveSearch({ domain: 'logs', mode: 'lucene', rangeMs: 900000, where: 'timeout OR failed', filters });
    const edited = editSearchExpression(text, filters);
    const restored = effectiveSearch({ domain: 'logs', mode: 'lucene', rangeMs: 900000, where: edited.where, filters: edited.filters });
    expect(restored).toEqual(split);
    expect(Object.values(restored.parameters)).toEqual(['%timeout%', '%failed%', 'api', 'worker']);
  });
});
