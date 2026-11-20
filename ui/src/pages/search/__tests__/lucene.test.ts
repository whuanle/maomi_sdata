import { describe, expect, it } from 'vitest';
import { LuceneCompileError, buildFilterConditionSql, compileLucene, resolveTokenContext } from '../lucene';

describe('compileLucene', () => {
  it('空输入返回空片段', () => {
    const result = compileLucene('   ', 'logs');
    expect(result.sql).toBe('');
    expect(result.params).toEqual({});
  });

  it('单字段等值条件与参数绑定', () => {
    const result = compileLucene('severity:ERROR', 'logs');
    expect(result.sql).toBe('severity = $p0');
    expect(result.params).toEqual({ p0: 'ERROR' });
  });

  it('多个字段条件以 AND 串联', () => {
    const result = compileLucene('service:api severity:ERROR', 'logs');
    expect(result.sql).toBe('service = $p0 AND severity = $p1');
    expect(result.params).toEqual({ p0: 'api', p1: 'ERROR' });
  });

  it('OR 关键字与括号分组', () => {
    const result = compileLucene('service:api OR (severity:ERROR severity:FATAL)', 'logs');
    expect(result.sql).toBe('service = $p0 OR (severity = $p1 AND severity = $p2)');
  });

  it('-field:value 编译为 NOT', () => {
    const result = compileLucene('-severity:DEBUG', 'logs');
    expect(result.sql).toBe('NOT (severity = $p0)');
    expect(result.params).toEqual({ p0: 'DEBUG' });
  });

  it('引号短语作为等值', () => {
    const result = compileLucene('service:"api gateway"', 'logs');
    expect(result.sql).toBe('service = $p0');
    expect(result.params).toEqual({ p0: 'api gateway' });
  });

  it('裸词与短语全文匹配 body（LIKE 转义通配符）', () => {
    const result = compileLucene('timeout "连接 失败"', 'logs');
    expect(result.sql).toBe('body LIKE $p0 AND body LIKE $p1');
    expect(result.params).toEqual({ p0: '%timeout%', p1: '%连接 失败%' });
  });

  it('traces 裸词匹配 name/service/trace_id 多列', () => {
    const result = compileLucene('timeout', 'traces');
    expect(result.sql).toBe('(name LIKE $p0 OR service LIKE $p0 OR trace_id LIKE $p0)');
  });

  it('前缀匹配 field:pre* 编译为 starts_with', () => {
    const result = compileLucene('service:web-*', 'logs');
    expect(result.sql).toBe('starts_with(service, $p0)');
    expect(result.params).toEqual({ p0: 'web-' });
  });

  it('数值字段比较操作符经 TRY_CAST', () => {
    const result = compileLucene('duration_us:>=1000.5 status:error', 'traces');
    expect(result.sql).toBe('TRY_CAST(duration_us AS DOUBLE) >= $p0 AND status = $p1');
    expect(result.params).toEqual({ p0: 1000.5, p1: 'error' });
  });

  it('点号字面 key 字段编译为 json_valid 防护 + 绑定路径提取（路径参数化）', () => {
    const result = compileLucene('attributes.http.method:POST', 'logs');
    expect(result.sql).toBe('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END, $jp0) = $p1');
    expect(result.params).toEqual({ jp0: '$."http.method"', p1: 'POST' });
  });

  it('点分 JSON key（order.no）按字面单层 key 提取，不做嵌套展开', () => {
    const result = compileLucene('attributes.order.no:PAY-2024', 'logs');
    expect(result.sql).toBe('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END, $jp0) = $p1');
    expect(result.params).toEqual({ jp0: '$."order.no"', p1: 'PAY-2024' });
  });

  it('括号嵌套路径逐层提取（对象 key + 数组索引），与字面点分 key 互不串查', () => {
    const result = compileLucene('attributes["http"]["status_code"]:500', 'logs');
    expect(result.sql).toBe('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END, $jp0) = $p1');
    expect(result.params).toEqual({ jp0: '$."http"."status_code"', p1: '500' });

    const indexed = compileLucene('body["items"][0]["name"]:ok', 'logs');
    expect(indexed.params).toEqual({ jp0: '$."items"[0]."name"', p1: 'ok' });
  });

  it('括号路径 key 含空格/引号内冒号等特殊字符（tokenize 引号模式保词）', () => {
    const result = compileLucene('attributes["my key"]:v', 'logs');
    expect(result.params).toEqual({ jp0: '$."my key"', p1: 'v' });
  });

  it('未知字段报错并列出可用字段', () => {
    expect(() => compileLucene('nope:1', 'logs')).toThrow(LuceneCompileError);
    expect(() => compileLucene('nope:1', 'logs')).toThrow(/可用字段/);
  });

  it('time 字段拒绝属性匹配', () => {
    expect(() => compileLucene('ts:2026', 'logs')).toThrow(/时间范围选择器/);
  });

  it('json 根字段要求子路径', () => {
    expect(() => compileLucene('attributes:x', 'logs')).toThrow(/子路径/);
  });

  it('数值字段前缀匹配报错', () => {
    expect(() => compileLucene('duration_us:12*', 'traces')).toThrow(/前缀/);
  });

  it('字符串字段比较操作符报错', () => {
    expect(() => compileLucene('severity:>5', 'logs')).toThrow(/数值字段/);
  });

  it('语法错误：未闭合括号与未闭合引号', () => {
    expect(() => compileLucene('(severity:ERROR', 'logs')).toThrow(/括号未闭合/);
    expect(() => compileLucene('phrase "未闭合', 'logs')).toThrow(/引号未闭合/);
    expect(() => compileLucene('severity:', 'logs')).toThrow(/缺少值/);
  });

  it('非 JSON 字段不支持点路径', () => {
    expect(() => compileLucene('service.extra:x', 'logs')).toThrow(/未知字段/);
  });

  it('JSON key 含引号/反斜杠时拒绝（防 JSON 路径逃逸）', () => {
    expect(() => compileLucene('attributes.a"b:x', 'logs')).toThrow(/JSON 路径不合法|引号未闭合/);
    expect(() => compileLucene('attributes.a\\b:x', 'logs')).toThrow(/JSON 路径不合法/);
  });
});

describe('buildFilterConditionSql', () => {
  it('eq/neq 标量条件绑定 $pN', () => {
    const params = {};
    expect(buildFilterConditionSql('service', 'eq', 'api', 'logs', params)).toBe('service = $p0');
    expect(params).toEqual({ p0: 'api' });
    expect(buildFilterConditionSql('severity', 'neq', 'DEBUG', 'logs', params)).toBe('severity != $p1');
  });

  it('in/nin 值列表编译为 IN/NOT IN（逐值参数绑定）', () => {
    const params = {};
    expect(buildFilterConditionSql('severity', 'in', ['ERROR', 'WARN', 'FATAL'], 'logs', params)).toBe('severity IN ($p0, $p1, $p2)');
    expect(params).toEqual({ p0: 'ERROR', p1: 'WARN', p2: 'FATAL' });
    expect(buildFilterConditionSql('service', 'nin', ['gateway'], 'logs', params)).toBe('service NOT IN ($p3)');
  });

  it('JSON 字面 key 的 in/nin 走防护 + 绑定路径提取', () => {
    const params = {};
    expect(buildFilterConditionSql('attributes.order.no', 'in', ['A-100', 'B-200'], 'logs', params))
      .toBe('json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END, $jp0) IN ($p1, $p2)');
    expect(params).toEqual({ jp0: '$."order.no"', p1: 'A-100', p2: 'B-200' });
  });

  it('数值字段 in 列表逐值校验并经 TRY_CAST', () => {
    const params = {};
    expect(buildFilterConditionSql('duration_us', 'in', ['1000', '2000'], 'traces', params))
      .toBe('TRY_CAST(duration_us AS DOUBLE) IN ($p0, $p1)');
    expect(params).toEqual({ p0: 1000, p1: 2000 });
    expect(() => buildFilterConditionSql('duration_us', 'in', ['abc'], 'traces', {})).toThrow(/数值/);
  });

  it('exists/nexists 编译为 IS NOT NULL / IS NULL（JSON key 走防护路径）', () => {
    expect(buildFilterConditionSql('severity', 'exists', '', 'logs', {})).toBe('severity IS NOT NULL');
    expect(buildFilterConditionSql('severity', 'nexists', '', 'logs', {})).toBe('severity IS NULL');
    expect(buildFilterConditionSql('resource.service.name', 'exists', '', 'logs', {}))
      .toBe('json_extract_string(CASE WHEN json_valid(resource) THEN resource ELSE NULL END, $jp0) IS NOT NULL');
    expect(buildFilterConditionSql('body["k"]', 'nexists', '', 'logs', {}))
      .toBe('json_extract_string(CASE WHEN json_valid(body) THEN body ELSE NULL END, $jp0) IS NULL');
  });

  it('in 值列表超限/为空报错', () => {
    expect(() => buildFilterConditionSql('severity', 'in', [], 'logs', {})).toThrow(/至少需要一个值/);
    const tooMany = Array.from({ length: 21 }, (_, i) => `v${i}`);
    expect(() => buildFilterConditionSql('severity', 'in', tooMany, 'logs', {})).toThrow(/值过多/);
  });

  it('标量操作符不接受值列表', () => {
    expect(() => buildFilterConditionSql('service', 'eq', ['a', 'b'], 'logs', {})).toThrow(/不接受值列表/);
    expect(() => buildFilterConditionSql('severity', 'exists', ['a'], 'logs', {})).toThrow(/不接受值列表/);
  });
});

describe('resolveTokenContext（WHERE 输入建议）', () => {
  it('裸词 → 字段阶段，替换区间为词元本身', () => {
    const context = resolveTokenContext('sev severity:x', 3, 'logs');
    expect(context.phase).toBe('field');
    expect(context.typed).toBe('sev');
    expect(context.start).toBe(0);
    expect(context.end).toBe(3);
  });

  it('field: 之后 → 值阶段并携带字段与 kind', () => {
    expect(resolveTokenContext('severity:ER', 11, 'logs')).toMatchObject({ phase: 'value', field: 'severity', kind: 'string', typed: 'ER' });
    expect(resolveTokenContext('duration_us:1', 12, 'traces')).toMatchObject({ phase: 'value', field: 'duration_us', kind: 'number' });
  });

  it('短语值进行中剥离引导引号；NOT 前导 - 剥离', () => {
    expect(resolveTokenContext('severity:"conn', 14, 'logs')).toMatchObject({ phase: 'value', field: 'severity', typed: 'conn' });
    expect(resolveTokenContext('-sev', 4, 'logs')).toMatchObject({ phase: 'field', typed: 'sev' });
  });

  it('JSON key 内冒号不切分（方括号深度保护）', () => {
    expect(resolveTokenContext('attributes["time:stamp"', 23, 'logs')).toMatchObject({ phase: 'field' });
    expect(resolveTokenContext('attributes["time:stamp"]:v', 26, 'logs')).toMatchObject({ phase: 'value', field: 'attributes["time:stamp"]' });
  });

  it('不完整括号前缀 → jsonPrefix（子属性建议），完整段进 segments', () => {
    expect(resolveTokenContext('attributes["http"]["st', 22, 'logs')).toEqual({
      phase: 'field',
      jsonPrefix: { root: 'attributes', segments: ['http'] },
      typed: 'st',
      start: 0,
      end: 22,
    });
    expect(resolveTokenContext('attributes[', 11, 'logs')).toMatchObject({ jsonPrefix: { root: 'attributes', segments: [] }, typed: '' });
    // 索引段与尾随数组索引
    expect(resolveTokenContext('body["items"][0]["', 17, 'logs')).toMatchObject({ jsonPrefix: { root: 'body', segments: ['items', 0] }, typed: '' });
    // 非法/非 JSON 根前缀无 jsonPrefix
    expect(resolveTokenContext('service["x"', 11, 'logs').jsonPrefix).toBeUndefined();
  });
});
