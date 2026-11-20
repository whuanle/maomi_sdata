import { bindBarWhere } from './barWhere';
import i18n from '@/i18n';
import { SEARCH_FIELDS } from '@/api/search';
import type { SearchSqlParameters } from '@/api/types';
import { buildFilterConditionSql, compileLucene, LuceneCompileError } from '@/pages/search/lucene';
import { jsonFieldExpression, parseJsonField, quoteFieldAlias, splitFieldList } from '@/pages/search/jsonFields';
import { DOMAIN_COLUMNS, type ChartSource } from './buildChartSql';

export interface EventSearchConfig {
  domain: ChartSource;
  fields: string;
  syntax: 'lucene' | 'sql';
  where: string;
  orderBy: string;
  filters: { field: string; value: unknown; exists?: boolean }[];
}

export const EVENT_DEFAULT_FIELDS: Record<ChartSource, string> = {
  iot: 'ts, device_key, signal, num_value, str_value, quality',
  logs: 'ts, service, severity, body',
  traces: 'ts, service, name, duration_us, status',
  metrics: 'ts, name, value, labels',
  sessions: 'ts, project_key, session_id, user_agent, domain',
};

export function eventFields(domain: ChartSource): string[] {
  return domain === 'logs' || domain === 'traces'
    ? SEARCH_FIELDS[domain].map((item) => item.field)
    : DOMAIN_COLUMNS[domain];
}

/** 列名只接受领域白名单或已有 JSON 路径语法；外部值和路径始终绑定参数. */
export function buildEventSearch(config: EventSearchConfig) {
  const { domain } = config;
  const supported = domain === 'logs' || domain === 'traces';
  const parameters: SearchSqlParameters = {};
  const conditions: string[] = [];
  if (config.where.trim()) {
    if (config.syntax === 'lucene') {
      const compiled = compileLucene(config.where, domain);
      Object.assign(parameters, compiled.params);
      if (compiled.sql) conditions.push(`(${compiled.sql})`);
    } else conditions.push(`(${bindBarWhere(config.where.trim(), parameters)})`);
  }
  const schema = eventFields(domain);
  const expression = (field: string): string => {
    if (schema.includes(field)) return field;
    const path = supported ? parseJsonField(field, domain) : null;
    if (path) return jsonFieldExpression(path, parameters);
    throw new LuceneCompileError(i18n.t('charts.eventUnknownField', { field }));
  };
  const columns = [...new Set(splitFieldList(config.fields))];
  if (!columns.length) throw new LuceneCompileError(i18n.t('charts.eventFieldsEmpty'));
  const select = columns.map((field) => `${expression(field)} AS ${quoteFieldAlias(field)}`);
  const identity = domain === 'logs'
    ? { ts: 'ts', id: 'CAST(id AS VARCHAR) AS id' }
    : domain === 'traces' ? { ts: 'ts', service: 'service', trace_id: 'trace_id', span_id: 'span_id' } : domain === 'sessions' ? { project_key: 'project_key', session_id: 'session_id' } : {};
  for (const [field, sql] of Object.entries(identity)) {
    if (!columns.includes(field)) select.push(sql);
  }
  const ordered = new Set<string>();
  const order = splitFieldList(config.orderBy).map((part) => {
    const match = part.match(/^(.+?)(?:\s+(asc|desc))?$/i);
    if (!match) throw new LuceneCompileError(i18n.t('charts.eventOrderInvalid'));
    const field = match[1].trim();
    ordered.add(field);
    return `${expression(field)} ${match[2]?.toUpperCase() ?? 'ASC'}`;
  });
  if (!order.length) order.push('ts DESC');
  const ties = domain === 'logs' ? ['id'] : schema;
  order.push(...ties.filter((field) => !ordered.has(field)).map((field) => `${field} ASC`));
  for (const { field, value, exists } of config.filters) {
    if (supported) {
      conditions.push(buildFilterConditionSql(field, exists ? 'exists' : value == null ? 'nexists' : 'eq',
        typeof value === 'object' ? JSON.stringify(value) : String(value ?? ''), domain, parameters));
    } else {
      const column = expression(field);
      if (exists) conditions.push(`${column} IS NOT NULL`);
      else if (value == null) conditions.push(`${column} IS NULL`);
      else {
        const key = `event_filter_${Object.keys(parameters).length}`;
        parameters[key] = typeof value === 'object' ? JSON.stringify(value) : value as string | number | boolean;
        conditions.push(`${column} = $${key}`);
      }
    }
  }
  const table = domain === 'sessions'
    ? '(SELECT project_key, session_id, min(ts) AS ts, arg_max(user_agent, ts) AS user_agent, arg_max(domain, ts) AS domain FROM {{sessions}} WHERE ts >= $start AND ts < $end GROUP BY project_key, session_id)'
    : `{{${domain}}}`;
  const base = `SELECT ${select.join(', ')}\nFROM ${table}\nWHERE ts >= $start AND ts < $end${conditions.length ? ' AND ' + conditions.join(' AND ') : ''}\nORDER BY ${order.join(', ')}`;
  return {
    columns, parameters,
    sql: `${base}\nLIMIT CAST($page_limit AS BIGINT) OFFSET CAST($page_offset AS BIGINT)`,
    panelSql: `${base}\nLIMIT 10000`,
  };
}
