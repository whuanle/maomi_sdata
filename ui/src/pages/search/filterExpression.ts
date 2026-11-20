import type { SearchFilter } from './filters';

const quote = (value: string) => JSON.stringify(value);

/** 筛选条件的可编辑 Lucene 表达式；字符串始终引用，避免通配符或布尔关键字改变含义. */
export function filterExpression(filter: SearchFilter): string {
  const dot = filter.field.indexOf('.');
  const field = dot > 0 && !filter.field.includes('[') && /[\s():]/.test(filter.field)
    ? `${filter.field.slice(0, dot)}[${quote(filter.field.slice(dot + 1))}]` : filter.field;
  const term = (value: string) => `${field}:${quote(value)}`;
  switch (filter.op) {
    case 'in':
    case 'nin': {
      const terms = (filter.values ?? []).map(term);
      const group = terms.length === 1 ? terms[0] : `(${terms.join(' OR ')})`;
      return filter.op === 'nin' ? `-${group}` : group;
    }
    case 'eq': return term(filter.value);
    case 'neq': return `-${term(filter.value)}`;
    case 'exists': return `${field}:*`;
    case 'nexists': return `-${field}:*`;
    case 'root': return '(-parent_span_id:* OR parent_span_id:"" OR parent_span_id:"0000000000000000")';
    case 'prefix': return `${term(filter.value)}*`;
    case 'contains': return `${field}:*${quote(filter.value)}*`;
    default: return `${field}:${{ gt: '>', gte: '>=', lt: '<', lte: '<=' }[filter.op]}${filter.value}`;
  }
}

export const DENOISE_EXPRESSION = '-(severity:"DEBUG" OR severity:"TRACE")';

/** 只拆最外层 AND，绝不把 OR 子树摊平成勾选项；未完成的输入整体保留为草稿. */
function conjuncts(text: string): string[] {
  const parts: string[] = [];
  let start = 0;
  let depth = 0;
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted && ch === '\\') { i++; continue; }
    if (ch === '"') quoted = !quoted;
    if (quoted) continue;
    if (ch === '(') depth++;
    if (ch === ')') depth--;
    if (depth < 0) return [text];
    if (depth === 0 && /\s/.test(ch)) {
      if (i > start) parts.push(text.slice(start, i));
      start = i + 1;
    }
  }
  if (quoted || depth !== 0) return [text];
  if (start < text.length) parts.push(text.slice(start));
  if (parts.some((part) => /^(OR|or|NOT|not|-)$/.test(part))) return [text];
  const isAnd = (part: string) => /^(AND|and)$/.test(part);
  if (parts.some((part, index) => isAnd(part) && (index === 0 || index === parts.length - 1 || isAnd(parts[index - 1])))) return [text];
  return parts.filter((part) => !isAnd(part));
}

function unwrap(text: string): string {
  if (!text.startsWith('(') || !text.endsWith(')')) return text;
  let depth = 0;
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    if (quoted && text[i] === '\\') { i++; continue; }
    if (text[i] === '"') quoted = !quoted;
    if (quoted) continue;
    if (text[i] === '(') depth++;
    if (text[i] === ')' && --depth === 0 && i !== text.length - 1) return text;
  }
  return depth === 0 && !quoted ? text.slice(1, -1) : text;
}

/** 仅在展示时合成：执行/URL/保存仍复用独立草稿与结构化筛选，不重复编译条件. */
export function searchExpression(where: string, filters: SearchFilter[], denoise = false): string {
  const clauses = filters.map(filterExpression);
  if (denoise) clauses.push(DENOISE_EXPRESSION);
  return [where.trim() ? (clauses.length ? `(${where.trim()})` : where) : '', ...clauses].filter(Boolean).join(' ');
}

/** 完整输入编辑后，保留未动过的自动子句；改写/删除的子句交还文本，清除隐藏筛选. */
export function editSearchExpression(text: string, filters: SearchFilter[], denoise = false) {
  const parts = conjuncts(text.trim());
  const retained = filters.filter((filter) => {
    const index = parts.indexOf(filterExpression(filter));
    if (index < 0) return false;
    parts.splice(index, 1);
    return true;
  });
  const denoiseIndex = denoise ? parts.indexOf(DENOISE_EXPRESSION) : -1;
  if (denoiseIndex >= 0) parts.splice(denoiseIndex, 1);
  const where = retained.length === 0 && denoiseIndex < 0 ? text
    : parts.length === 1 ? unwrap(parts[0]) : parts.join(' ');
  return { where, filters: retained, denoise: denoiseIndex >= 0 };
}
