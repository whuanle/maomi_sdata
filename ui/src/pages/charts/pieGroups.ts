import { SEARCH_FIELDS } from '@/api/search';
import type { ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { jsonFieldExpression, parseJsonField, quoteFieldAlias } from '@/pages/search/jsonFields';

/** 分类保留原字段身份，JSON 路径参数绑定，不能与度量列重名. */
export function pieGroupColumns(source: ChartSource, groups: string[], parameters: SearchSqlParameters) {
  if (new Set(groups).size !== groups.length) throw new Error('请勿重复选择饼图分组字段');
  return groups.map((field) => {
    if (field === 'value') throw new Error('value 是饼图度量列，请选择其他分类字段');
    const physical = SEARCH_FIELDS[source].find((item) => item.field === field && item.kind !== 'time' && item.kind !== 'json');
    const json = parseJsonField(field, source);
    if (!physical && !json) throw new Error('未知分组字段：' + field);
    const alias = quoteFieldAlias(field);
    return { select: json ? jsonFieldExpression(json, parameters) + ' AS ' + alias : alias, group: alias };
  });
}
