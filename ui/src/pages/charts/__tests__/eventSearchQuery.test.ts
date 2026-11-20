import { describe, expect, it } from 'vitest';
import '@/i18n';
import { buildEventSearch, EVENT_DEFAULT_FIELDS, type EventSearchConfig } from '../eventSearchQuery';

const config: EventSearchConfig = { domain: 'logs', fields: EVENT_DEFAULT_FIELDS.logs, syntax: 'lucene', where: '', orderBy: 'ts DESC', filters: [] };

describe('eventSearchQuery', () => {
  it('binds Lucene values and includes string log identity without exposing pagination in saved SQL', () => {
    const result = buildEventSearch({ ...config, where: 'service:api', filters: [{ field: 'body', value: "quoted ' value" }] });
    expect(result.sql).toContain('CAST(id AS VARCHAR) AS id');
    expect(result.sql).toContain('id ASC');
    expect(result.sql).toContain('OFFSET CAST($page_offset AS BIGINT)');
    expect(result.sql).not.toContain("quoted ' value");
    expect(Object.values(result.parameters)).toContain("quoted ' value");
    expect(result.panelSql).not.toContain('$page_');
    expect(result.panelSql).toContain('LIMIT 10000');
  });

  it('keeps SELECT and Lucene JSON paths in separate bound parameters', () => {
    const result = buildEventSearch({ ...config, fields: 'ts, attributes.first', where: 'attributes.second:value' });
    expect(Object.values(result.parameters)).toContain('$."first"');
    expect(Object.values(result.parameters)).toContain('$."second"');
    expect(Object.values(result.parameters)).toContain('value');
  });

  it('validates selected fields and sorting instead of accepting SQL as an identifier', () => {
    expect(() => buildEventSearch({ ...config, fields: 'ts, (SELECT 1)' })).toThrow();
    expect(() => buildEventSearch({ ...config, orderBy: 'ts; DROP TABLE logs' })).toThrow();
    expect(() => buildEventSearch({ ...config, fields: '' })).toThrow();
  });

  it('keeps trace identity when the visible columns omit it', () => {
    const result = buildEventSearch({ ...config, domain: 'traces', fields: 'ts, name', syntax: 'sql', where: 'duration_us > 10' });
    expect(result.columns).toEqual(['ts', 'name']);
    expect(result.sql).toContain('trace_id');
    expect(result.sql).toContain('span_id');
    expect(result.sql).toContain('(duration_us > $cf0)');
    expect(result.parameters).toMatchObject({ cf0: 10 });
  });

  it.each([
    { domain: 'metrics' as const, where: 'name:"cpu\'usage" value:>10', value: "cpu'usage", field: 'name' },
    { domain: 'sessions' as const, where: 'domain:"app\'quoted" project_key:demo', value: "app'quoted", field: 'domain' },
  ])('compiles $domain Lucene with bound values', ({ domain, where, value, field }) => {
    const result = buildEventSearch({ ...config, domain, fields: EVENT_DEFAULT_FIELDS[domain], where });
    expect(result.sql).toContain(field);
    expect(result.sql).not.toContain(value);
    expect(Object.values(result.parameters)).toContain(value);
    expect(result.panelSql).not.toContain('$page_');
    if (domain === 'sessions') {
      expect(result.sql).toContain('GROUP BY project_key, session_id)\nWHERE');
      expect(result.sql).toContain('arg_max(domain, ts)');
    } else {
      expect(result.sql).toContain('TRY_CAST(value AS DOUBLE) > $');
      expect(Object.values(result.parameters)).toContain(10);
    }
  });

  it.each(['metrics', 'sessions'] as const)('supports %s bare terms and rejects unknown Lucene fields', (domain) => {
    const result = buildEventSearch({ ...config, domain, fields: EVENT_DEFAULT_FIELDS[domain], where: 'needle' });
    expect(Object.values(result.parameters)).toContain('%needle%');
    expect(result.sql).toContain(domain === 'metrics' ? 'name' : 'session_id');
    expect(() => buildEventSearch({ ...config, domain, fields: EVENT_DEFAULT_FIELDS[domain], where: 'severity:ERROR' })).toThrow();
  });

  it.each([
    { domain: 'metrics' as const, where: "name = 'cpu'", value: 'cpu' },
    { domain: 'sessions' as const, where: "domain = 'example.com'", value: 'example.com' },
  ])('keeps $domain SQL conditions parameterized', ({ domain, where, value }) => {
    const result = buildEventSearch({ ...config, domain, fields: EVENT_DEFAULT_FIELDS[domain], syntax: 'sql', where });
    expect(result.sql).not.toContain(value);
    expect(Object.values(result.parameters)).toContain(value);
  });

  it('uses the metric and session schemas, rejecting log-only fields', () => {
    expect(buildEventSearch({ ...config, domain: 'metrics', fields: EVENT_DEFAULT_FIELDS.metrics, syntax: 'sql' }).sql).toContain('FROM {{metrics}}');
    expect(buildEventSearch({ ...config, domain: 'sessions', fields: EVENT_DEFAULT_FIELDS.sessions, syntax: 'sql' }).sql).toContain('GROUP BY project_key, session_id');
    expect(() => buildEventSearch({ ...config, domain: 'metrics', fields: 'severity', syntax: 'sql' })).toThrow();
  });
});
