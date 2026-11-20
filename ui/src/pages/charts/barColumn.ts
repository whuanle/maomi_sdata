import { SEARCH_FIELDS } from '@/api/search';
import type { ChartSource } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { jsonFieldExpression, parseJsonField } from '@/pages/search/jsonFields';
import { bindChartParameter, parseFormula, seriesRef, type FormulaNode } from './formula';

/** Column 接受字段、JSON 路径和算术表达式；标识符校验，路径及常量绑定. */
export function barColumn(input: string, source: ChartSource, parameters: SearchSqlParameters, distinct = false): string {
  const expressions: string[] = [];
  const fields: { sql: string; numeric: boolean }[] = [];
  let remaining = input.trim();
  if (!remaining || remaining.length > 512) throw new Error('请输入 SQL Column（字段或算术表达式）');
  while (remaining) {
    const whitespace = remaining.match(/^\s+/);
    if (whitespace) { remaining = remaining.slice(whitespace[0].length); continue; }
    const number = remaining.match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?/);
    if (number) { expressions.push(number[0]); remaining = remaining.slice(number[0].length); continue; }
    const field = remaining.match(/^[A-Za-z_][A-Za-z0-9_]*(?:(?:\.[A-Za-z_][A-Za-z0-9_.]*)|(?:\[(?:"(?:[^"\\]|\\.)*"|\d+)\]))*/);
    if (field) {
      const name = field[0];
      const meta = SEARCH_FIELDS[source].find((item) => item.field === name && item.kind !== 'time' && item.kind !== 'json');
      const json = parseJsonField(name, source);
      if (!meta && !json) throw new Error('未知 Column 字段：' + name);
      const sql = meta ? '"' + meta.field + '"' : jsonFieldExpression(json!, parameters);
      expressions.push(seriesRef(fields.length));
      fields.push({ sql, numeric: meta?.kind === 'number' || !!json });
      remaining = remaining.slice(name.length);
      continue;
    }
    if (/^[()+*/-]/.test(remaining) || remaining[0] === '+') {
      expressions.push(remaining[0]); remaining = remaining.slice(1); continue;
    }
    throw new Error('Column 仅支持字段、JSON 路径与算术运算');
  }
  const refs = fields.map((_, index) => seriesRef(index));
  const tree = parseFormula(expressions.join(' '), refs);
  const emit = (node: FormulaNode): string => {
    if (node.kind === 'number') return 'CAST(' + bindChartParameter(parameters, node.value) + ' AS DOUBLE)';
    if (node.kind === 'ref') {
      const field = fields[refs.indexOf(node.value)];
      if (distinct && tree.kind === 'ref') return field.sql;
      if (!field.numeric) throw new Error('此聚合需要数值 Column');
      return 'TRY_CAST(' + field.sql + ' AS DOUBLE)';
    }
    if (node.kind === 'unary') return '(0 ' + node.op + ' ' + emit(node.child) + ')';
    const right = emit(node.right);
    return '(' + emit(node.left) + ' ' + node.op + ' ' + (node.op === '/' ? 'NULLIF(' + right + ', 0)' : right) + ')';
  };
  return emit(tree);
}
