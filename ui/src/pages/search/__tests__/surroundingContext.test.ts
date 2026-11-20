import { describe, expect, it } from 'vitest';
import { buildAvailableFields, buildContextQuery, CONTEXT_DEFAULT_WINDOW_MS, CONTEXT_WINDOWS } from '../surroundingContext';

const record = {
  ts: '2026-11-17T12:00:00.000000Z',
  service: 'api-gateway',
  severity: 'debug',
  body: '请求日志',
  trace_id: 't-1',
  span_id: 's-1',
  source: 'application',
  attributes: '{"http.route":"/api/orders","duration.ms":"637"}',
  resource: '{"host.name":"gateway-01"}',
};

describe('buildAvailableFields', () => {
  it('顶层列在前、attributes/resource 展开在后，空值剔除', () => {
    const fields = buildAvailableFields(record);
    expect(fields.map((field) => field.field)).toEqual([
      'service',
      'severity',
      'source',
      'trace_id',
      'span_id',
      'attributes["http.route"]',
      'attributes["duration.ms"]',
      'resource["host.name"]',
    ]);
    expect(fields[0]).toEqual({ field: 'service', key: 'service', value: 'api-gateway' });
    expect(fields[5]).toEqual({ field: 'attributes["http.route"]', key: 'http.route', value: '/api/orders' });
  });

  it('非法/空 JSON 与缺失列安全返回', () => {
    expect(buildAvailableFields({ ts: '2026-11-17T12:00:00Z', service: 's', attributes: 'not-json', resource: '' })).toEqual([
      { field: 'service', key: 'service', value: 's' },
    ]);
  });
});

describe('buildContextQuery', () => {
  it('traces 上下文保留微秒、主机和嵌套路径均参数绑定', () => {
    const anchor = { ...record, ts: '2026-11-17T12:00:00.123456Z', name: 'request', attributes: '{"http":{"code":504}}' };
    const request = buildContextQuery({ record: anchor, domain: 'traces', windowMs: 100, mode: 'host', matching: [] });
    expect(request.sql).toContain('FROM {{traces}}');
    expect(request.sql).toContain('duration_us');
    expect(request.from).toBe('2026-11-17T12:00:00.023456Z');
    expect(request.to).toBe('2026-11-17T12:00:00.223456Z');
    expect(request.parameters).toMatchObject({ host: 'gateway-01' });
    expect(request.sql).not.toContain('gateway-01');
    const fields = buildAvailableFields(anchor, 'traces');
    expect(fields).toContainEqual({ field: 'attributes["http"]["code"]', key: 'http.code', value: '504' });
    expect(fields.some((field) => field.field === 'severity')).toBe(false);
    expect(() => buildContextQuery({ record: {}, domain: 'traces', windowMs: 100, mode: 'host', matching: [] })).toThrow('Missing host');
    expect(() => buildContextQuery({ record: anchor, domain: 'traces', windowMs: 100, mode: 'attrs', matching: [{ field: 'severity', key: 'severity', value: 'error' }] })).toThrow();
  });

  it('默认任意匹配：仅时间窗条件，from/to 为 ±窗，无命名参数', () => {
    const request = buildContextQuery({ record, windowMs: CONTEXT_DEFAULT_WINDOW_MS, mode: 'anything', matching: [] });
    expect(request.sql).toContain('WHERE ts >= $start AND ts < $end');
    expect(request.sql).not.toContain('$m0');
    expect(request.from).toBe('2026-11-17T11:59:30.000000Z');
    expect(request.to).toBe('2026-11-17T12:00:30.000000Z');
    expect(request.parameters).toEqual({ page_limit: 101, page_offset: 0 });
    expect(request.sql).toContain('LIMIT CAST($page_limit AS BIGINT) OFFSET CAST($page_offset AS BIGINT)');
    expect(request.sql).toContain('CAST(id AS VARCHAR) AS id');
    expect(request.sql).toContain('ORDER BY ts ASC, id ASC NULLS LAST');
  });

  it('同服务模式追加 service = $service（参数绑定）', () => {
    const request = buildContextQuery({ record, windowMs: 5000, mode: 'service', matching: [] });
    expect(request.sql).toContain('service = $service');
    expect(request.parameters).toMatchObject({ service: 'api-gateway' });
    expect(request.from).toBe('2026-11-17T11:59:55.000000Z');
  });

  it('属性匹配：json_extract_string 路径编译 + $mN 值绑定（含带点 key）', () => {
    const request = buildContextQuery({
      record,
      windowMs: 1000,
      mode: 'attrs',
      matching: [
        { field: 'attributes["http.route"]', key: 'http.route', value: '/api/orders' },
        { field: 'severity', key: 'severity', value: 'debug' },
      ],
    });
    expect(request.sql).toMatch(/json_extract_string\(CASE WHEN json_valid\(attributes\) THEN attributes ELSE NULL END, \$jp\d+\) = \$m0/);
    expect(request.sql).toContain('severity = $m1');
    expect(request.parameters).toMatchObject({ jp3: '$."http.route"', m0: '/api/orders', m1: 'debug' });
  });

  it('时间窗档位覆盖 HyperDX 8 档（默认 30s）', () => {
    expect(CONTEXT_WINDOWS.map((option) => option.label)).toEqual(['100ms', '500ms', '1s', '5s', '30s', '1m', '5m', '15m']);
    expect(CONTEXT_DEFAULT_WINDOW_MS).toBe(30_000);
  });
});
