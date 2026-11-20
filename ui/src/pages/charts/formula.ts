import type { SearchSqlParameters } from '@/api/types';

export type FormulaNode =
  | { kind: 'number'; value: number }
  | { kind: 'ref'; value: string }
  | { kind: 'unary'; op: '+' | '-'; child: FormulaNode }
  | { kind: 'binary'; op: '+' | '-' | '*' | '/'; left: FormulaNode; right: FormulaNode };

export class FormulaError extends Error {
  constructor(public readonly key: string, public readonly ref = '') { super(key); }
}

/** 仅接受算术；不执行用户代码，要求完整消费所有输入. */
export function parseFormula(expression: string, refs: string[]): FormulaNode {
  if (!expression.trim()) throw new FormulaError('formulaEmpty');
  if (expression.length > 512) throw new FormulaError('formulaComplex');
  const tokens = expression.match(/(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|[A-Z]+|[^\s]/g) ?? [];
  if (tokens.length > 128) throw new FormulaError('formulaComplex');
  let position = 0;
  const primary = (depth: number): FormulaNode => {
    if (depth > 24) throw new FormulaError('formulaComplex');
    const token = tokens[position++];
    if (token === '+' || token === '-') return { kind: 'unary', op: token, child: primary(depth + 1) };
    if (token === '(') {
      const node = sum(depth + 1);
      if (tokens[position++] !== ')') throw new FormulaError('formulaSyntax');
      return node;
    }
    if (token && /^(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(token)) {
      const value = Number(token);
      if (!Number.isFinite(value)) throw new FormulaError('formulaSyntax');
      return { kind: 'number', value };
    }
    if (token && /^[A-Z]+$/.test(token)) {
      if (!refs.includes(token)) throw new FormulaError('formulaUnknown', token);
      return { kind: 'ref', value: token };
    }
    throw new FormulaError('formulaSyntax');
  };
  const product = (depth: number): FormulaNode => {
    let node = primary(depth);
    while (tokens[position] === '*' || tokens[position] === '/') {
      const op = tokens[position++] as '*' | '/';
      node = { kind: 'binary', op, left: node, right: primary(depth) };
    }
    return node;
  };
  const sum = (depth: number): FormulaNode => {
    let node = product(depth);
    while (tokens[position] === '+' || tokens[position] === '-') {
      const op = tokens[position++] as '+' | '-';
      node = { kind: 'binary', op, left: node, right: product(depth) };
    }
    return node;
  };
  const node = sum(0);
  if (position !== tokens.length) throw new FormulaError('formulaSyntax');
  return node;
}

/** SQL 结构仅来自 AST 白名单，数字作为参数传递. */
export function formulaSql(node: FormulaNode, refs: string[], bind: (value: number) => string): string {
  if (node.kind === 'number') return `CAST(${bind(node.value)} AS DOUBLE)`;
  if (node.kind === 'ref') return `v${refs.indexOf(node.value)}`;
  if (node.kind === 'unary') return `(0 ${node.op} ${formulaSql(node.child, refs, bind)})`;
  const left = formulaSql(node.left, refs, bind);
  const right = formulaSql(node.right, refs, bind);
  return node.op === '/' ? `(${left} / NULLIF(${right}, 0))` : `(${left} ${node.op} ${right})`;
}

export function bindChartParameter(parameters: SearchSqlParameters, value: string | number): string {
  const existing = Object.entries(parameters).find(([key, current]) => /^cf\d+$/.test(key) && current === value);
  if (existing) return `$${existing[0]}`;
  if (Object.keys(parameters).length >= 64) throw new FormulaError('formulaComplex');
  let index = 0;
  while (Object.prototype.hasOwnProperty.call(parameters, `cf${index}`)) index++;
  const key = `cf${index}`;
  parameters[key] = value;
  return `$${key}`;
}

export function seriesRef(index: number): string {
  let value = index + 1;
  let ref = '';
  while (value > 0) { value--; ref = String.fromCharCode(65 + value % 26) + ref; value = Math.floor(value / 26); }
  return ref;
}
