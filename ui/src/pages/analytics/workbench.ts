import type { ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { compileLucene } from '../search/lucene';

export const SOURCES: ChartSource[] = ['logs', 'traces', 'metrics', 'iot', 'sessions'];

export interface WorkbenchDefinition {
  version: 1;
  mode: 'sql' | 'lucene';
  source: ChartSource;
  sql: string;
  lucene: string;
  rowLimit: number;
  rangeMs: number;
  parameters: SearchSqlParameters;
}

export function parseParameters(text: string): SearchSqlParameters {
  const value: unknown = JSON.parse(text);
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('analytics.invalidParameters');
  const entries = Object.entries(value);
  if (entries.length > 64 || entries.some(([key, item]) =>
    !/^[a-z][a-z0-9_]*$/.test(key) || (item !== null && !['string', 'number', 'boolean'].includes(typeof item)))) {
    throw new Error('analytics.invalidParameters');
  }
  return value as SearchSqlParameters;
}

export function parseWorkbench(text: string): WorkbenchDefinition {
  const value = JSON.parse(text) as WorkbenchDefinition;
  if (!value || value.version !== 1 || !['sql', 'lucene'].includes(value.mode) || !SOURCES.includes(value.source)
    || typeof value.sql !== 'string' || typeof value.lucene !== 'string'
    || !Number.isInteger(value.rowLimit) || value.rowLimit < 1 || value.rowLimit > 10000
    || !Number.isFinite(value.rangeMs) || value.rangeMs <= 0 || value.rangeMs > 365 * 86400000) {
    throw new Error('analytics.invalidSnapshot');
  }
  parseParameters(JSON.stringify(value.parameters));
  return value;
}

export function luceneQuery(source: ChartSource, text: string) {
  if (!SOURCES.includes(source)) throw new Error('analytics.invalidSnapshot');
  const compiled = compileLucene(text, source);
  return {
    sql: `SELECT * FROM {{${source}}}\nWHERE ts >= $start AND ts < $end${compiled.sql ? ` AND (${compiled.sql})` : ''}\nORDER BY ts DESC`,
    parameters: compiled.params,
  };
}

export function quoteIdentifier(name: string) {
  return '"' + name.replace(/"/g, '""') + '"';
}
