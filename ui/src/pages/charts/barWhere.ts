import type { SearchSqlParameters } from '@/api/types';
import { bindChartParameter } from './formula';

/** SQL 条件保留表达式结构，字符串和数值字面量转为命名参数；拒绝语句/注释边界. */
export function bindBarWhere(input: string, parameters: SearchSqlParameters, allowBoundParameters = false): string {
  let output = '';
  let position = 0;
  while (position < input.length) {
    const rest = input.slice(position);
    if (allowBoundParameters && rest[0] === '$') {
      const parameter = rest.match(/^\$([a-z][a-z0-9_]*)\b/);
      if (!parameter || !['start', 'end'].includes(parameter[1]) && !Object.prototype.hasOwnProperty.call(parameters, parameter[1])) throw new Error('条件包含未绑定参数');
      output += parameter[0];
      position += parameter[0].length;
      continue;
    }
    if (/^(;|--|\/\*|\*\/|\$)/.test(rest)) throw new Error('条件仅允许单个 SQL 表达式，不支持注释或未绑定参数');
    if (rest[0] === "'") {
      const match = rest.match(/^'(?:[^']|'')*'/);
      if (!match) throw new Error('SQL 字符串引号未闭合');
      output += bindChartParameter(parameters, match[0].slice(1, -1).replace(/''/g, "'"));
      position += match[0].length;
      continue;
    }
    if (rest[0] === '"') {
      const match = rest.match(/^"(?:[^"]|"")*"/);
      if (!match) throw new Error('SQL 标识符引号未闭合');
      output += match[0];
      position += match[0].length;
      continue;
    }
    const word = rest.match(/^[A-Za-z_][A-Za-z0-9_]*/);
    if (word) {
      output += word[0];
      position += word[0].length;
      continue;
    }
    const number = rest.match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?/);
    if (number) {
      const value = Number(number[0]);
      if (!Number.isFinite(value)) throw new Error('SQL 数值必须为有限值');
      output += bindChartParameter(parameters, value);
      position += number[0].length;
      continue;
    }
    output += input[position++];
  }
  return output;
}
