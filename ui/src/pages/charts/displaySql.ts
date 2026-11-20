import type { SearchSqlParameters } from '@/api/types';

function literal(value: SearchSqlParameters[string]): string | null {
  if (value === null) return 'NULL';
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE';
  if (typeof value === 'number') return Number.isFinite(value) ? String(value) : null;
  return "'" + value.replace(/'/g, "''") + "'";
}

/** 仅供预览和复制；执行与保存必须继续使用原始 SQL 和绑定参数. */
export function displaySql(sql: string, parameters: SearchSqlParameters): string {
  let output = '';
  let position = 0;
  while (position < sql.length) {
    const rest = sql.slice(position);
    const quoted = rest.match(/^(?:'(?:[^']|'')*'|"(?:[^"]|"")*"|--[^\r\n]*|\/\*[\s\S]*?\*\/)/);
    if (quoted) {
      output += quoted[0];
      position += quoted[0].length;
      continue;
    }
    const dollarQuote = rest.match(/^\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$/);
    if (dollarQuote) {
      const end = sql.indexOf(dollarQuote[0], position + dollarQuote[0].length);
      if (end < 0) return output + rest;
      const length = end + dollarQuote[0].length - position;
      output += sql.slice(position, position + length);
      position += length;
      continue;
    }
    const parameter = rest.match(/^\$([A-Za-z_][A-Za-z0-9_]*)\b/);
    if (parameter && (position === 0 || !/[A-Za-z0-9_$]/.test(sql[position - 1]))) {
      const name = parameter[1];
      const value = !['start', 'end'].includes(name) && Object.prototype.hasOwnProperty.call(parameters, name) ? literal(parameters[name]) : null;
      output += value ?? parameter[0];
      position += parameter[0].length;
      continue;
    }
    output += sql[position++];
  }
  return output;
}
