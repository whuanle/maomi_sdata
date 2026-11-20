import type { SavedSearchDefinition, SearchSqlParameters } from '@/api/types';
import { buildFilterConditionSql, compileLucene, DENOISE_SQL } from './lucene';

/** 搜索与告警共用有效条件，不包含投影、排序、分页或绝对时间. */
export function effectiveSearch(definition: SavedSearchDefinition, parameters: SearchSqlParameters = {}) {
  const conditions: string[] = [];
  if (definition.mode === 'sql') {
    if (definition.where?.trim()) conditions.push(`(${definition.where.trim()})`);
  } else {
    const compiled = compileLucene(definition.where ?? '', definition.domain, parameters);
    Object.assign(parameters, compiled.params);
    if (compiled.sql) conditions.push(`(${compiled.sql})`);
  }
  for (const filter of definition.filters ?? []) {
    conditions.push(buildFilterConditionSql(filter.field, filter.op, filter.values ?? filter.value, definition.domain, parameters));
  }
  if (definition.denoise && definition.domain === 'logs') conditions.push(DENOISE_SQL);
  return { condition: conditions.join(' AND '), parameters };
}
