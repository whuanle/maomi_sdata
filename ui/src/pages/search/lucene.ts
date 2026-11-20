import { SEARCH_FIELDS } from '@/api/search';
import { jsonFieldExpression, jsonRoots, parseJsonField } from './jsonFields';
import type { SearchSqlParameters } from '@/api/types';
import type { ChartSource as SearchDomain } from '@/api/chartTypes';

/**
 * Lucene 风格属性搜索编译器（HyperDX 式搜索栏）：
 * 把 `service:api severity:ERROR` 等表达式编译为参数化 WHERE 片段（$p0..$pN 占位），
 * 值一律走服务端命名参数绑定（DuckDBParameter），绝不拼接进 SQL 文本。
 *
 * 语法：`field:value`（等值）/ `field:>n`（数值比较，支持 > >= < <=）/ `field:pre*`（前缀）/
 * `field:"多词值"`（等值短语）/ `-field:value`（NOT）/ 裸词或 `"短语"`（body 子串匹配）/
 * 子句间空白 = AND / `OR` 关键字 / 括号分组。JSON 路径字段（attributes.xx.y）经 json_extract_string 提取，
 * 整个后缀按字面单层 key 处理（点分 key 如 order.no 直取，与后端 QueryDslCompiler 一致）。
 * `field:*` 表示存在（含数值字段），`-field:*` 表示不存在；引用值支持转义与空字符串。
 * 过滤器树另有 in/nin/exists/nexists 条件构建器（buildFilterConditionSql）。
 */

/** 编译产物：WHERE 增量片段（不含 ts 时间范围条件）+ 命名参数. */
export interface CompiledLucene {
  sql: string;
  params: SearchSqlParameters;
}

/** 编译错误（人类可读中文，供界面直接展示）. */
export class LuceneCompileError extends Error {}

/** 字段元信息（kind 与 SEARCH_FIELDS 一致）. */
type FieldKind = 'string' | 'number' | 'time' | 'json';

interface FieldMeta {
  expression: string;
  kind: FieldKind;
}

/**
 * JSON key 白名单：任意字符，仅禁止引号与反斜杠（拼入 JSON 路径前会被转义）.
 * 点分 key（order.no、http.status_code）按字面单层 key 处理，与后端 QueryDslCompiler 一致.
 */
export const JSON_KEY_RE = /^[^"'\\]+$/;

/**
 * JSON 字面 key → json_extract_string 表达式（key 须经 JSON_KEY_RE 校验）.
 * 路径整体双引号包裹（JSON 路径层），单引号 SQL 转义（DuckDB 字面量层），点分 key 直取单层.
 */
export function jsonPathExpression(column: string, key: string): string {
  const escaped = key.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/'/g, "''");
  return `json_extract_string(${column}, '$."${escaped}"')`;
}

/** 词法单元. */
interface Token {
  kind: 'word' | 'phrase' | 'lparen' | 'rparen';
  text: string;
}

/** 条件 AST 节点. */
type Node =
  | { type: 'and'; children: Node[] }
  | { type: 'or'; children: Node[] }
  | { type: 'not'; child: Node }
  | { type: 'condition'; sql: string };

/** 引用值只解码已支持的转义，兼容既有含普通反斜杠的查询. */
function unquote(value: string): string {
  return value.slice(1, -1).replace(/\\(u[0-9a-fA-F]{4}|["\\/bfnrt])/g, (match) => JSON.parse(`"${match}"`) as string);
}

/** 词法分析：产出 word/phrase/括号单元；word 内允许引号包裹空格（field:"multi word"）. */
function tokenize(input: string): Token[] {
  const tokens: Token[] = [];
  let i = 0;
  while (i < input.length) {
    const c = input[i];
    if (/\s/.test(c)) {
      i++;
      continue;
    }

    if (c === '(') {
      tokens.push({ kind: 'lparen', text: '(' });
      i++;
      continue;
    }

    if (c === ')') {
      tokens.push({ kind: 'rparen', text: ')' });
      i++;
      continue;
    }

    if (c === '"') {
      let close = i + 1;
      while (close < input.length) {
        if (input[close] === '\\') { close += 2; continue; }
        if (input[close] === '"') break;
        close++;
      }
      if (close >= input.length) close = -1;
      if (close < 0) {
        throw new LuceneCompileError('引号未闭合：短语搜索需要成对的英文双引号');
      }

      tokens.push({ kind: 'phrase', text: unquote(input.slice(i, close + 1)) });
      i = close + 1;
      continue;
    }

    // word：空格/括号终止；内部遇到引号切换为引号模式（允许空格），闭合后回到 word 模式
    const start = i;
    let inQuote = false;
    while (i < input.length) {
      const ch = input[i];
      if (ch === '\\' && inQuote) { i += 2; continue; }
      if (ch === '"') {
        inQuote = !inQuote;
        i++;
        continue;
      }

      if (!inQuote && (/\s/.test(ch) || ch === '(' || ch === ')')) {
        break;
      }

      i++;
    }

    if (inQuote) throw new LuceneCompileError('引号未闭合');
    tokens.push({ kind: 'word', text: input.slice(start, i) });
  }

  return tokens;
}

/** 生成参数占位符并登记绑定值（$p0..$pN）. */
function bindParam(params: SearchSqlParameters, value: string | number): string {
  let index = Object.keys(params).length;
  while (Object.prototype.hasOwnProperty.call(params, `p${index}`)) index++;
  const name = `p${index}`;
  params[name] = value;
  return `$${name}`;
}

/** LIKE 子串值转义（与 QueryDslCompiler.match 一致：% _ \ 全转义，两侧加 %）. */
function likeParam(params: SearchSqlParameters, raw: string): string {
  const escaped = raw.replace(/[\\%_]/g, (ch) => `\\${ch}`);
  return bindParam(params, `%${escaped}%`);
}

/** 解析字段引用：白名单校验 + JSON 字段（点号字面 key / 括号嵌套路径）统一为 json_valid 防护 + 绑定路径的 json_extract_string. */
function resolveField(field: string, domain: SearchDomain, params?: SearchSqlParameters): FieldMeta {
  const schema = SEARCH_FIELDS[domain];
  if (field.includes('[')) {
    const path = parseJsonField(field, domain);
    if (!path) {
      throw new LuceneCompileError(`JSON 路径不合法："${field}"（括号路径段需为带引号 key 或数组索引，key 不能包含裸引号）`);
    }

    return { expression: jsonFieldExpression(path, params ?? {}), kind: 'string' };
  }

  if (field.includes('.')) {
    const path = parseJsonField(field, domain);
    if (!path) {
      // 点号语法延续 legacy 语义：JSON 根下的非法 key 明确报路径不合法，其余根回落到白名单检查（未知字段）
      const root = field.slice(0, field.indexOf('.'));
      const rootMeta = schema.find((meta) => meta.field === root);
      if (rootMeta?.kind === 'json') {
        throw new LuceneCompileError(`JSON 路径不合法："${field}"（key 不能包含引号或反斜杠）`);
      }
    } else {
      return { expression: jsonFieldExpression(path, params ?? {}), kind: 'string' };
    }
  }
  if (field.includes('.')) {
    const dot = field.indexOf('.');
    const root = field.slice(0, dot);
    const key = field.slice(dot + 1);
    const rootMeta = schema.find((meta) => meta.field === root);
    if (!rootMeta || rootMeta.kind !== 'json') {
      throw new LuceneCompileError(`未知字段 "${field}"，可用字段：${schema.map((meta) => meta.field).join(', ')}`);
    }

    if (key.length === 0 || !JSON_KEY_RE.test(key)) {
      throw new LuceneCompileError(`JSON 路径不合法："${field}"（key 不能包含引号或反斜杠）`);
    }

    return { expression: jsonPathExpression(root, key), kind: 'string' };
  }

  const meta = schema.find((candidate) => candidate.field === field);
  if (!meta) {
    throw new LuceneCompileError(`未知字段 "${field}"，可用字段：${schema.map((candidate) => candidate.field).join(', ')}`);
  }

  if (meta.kind === 'time') {
    throw new LuceneCompileError('时间字段 ts 请使用页面上的时间范围选择器，不支持属性匹配');
  }

  if (meta.kind === 'json') {
    throw new LuceneCompileError(`字段 ${field} 为 JSON，请指定子路径（如 ${field}.key）`);
  }

  return { expression: meta.field, kind: meta.kind };
}

/** 字段引用表达式（白名单校验 + JSON 字面 key 路径），供过滤器树 facet 聚合查询复用. @throws LuceneCompileError */
export function fieldExpression(field: string, domain: SearchDomain, params?: SearchSqlParameters): string {
  return resolveField(field, domain, params).expression;
}

/** 字段元信息（建议弹层用：kind 决定值阶段是否附加数值比较操作符候选）. */
export function fieldKind(field: string, domain: SearchDomain): FieldKind | null {
  try {
    return resolveField(field, domain).kind;
  } catch {
    return null;
  }
}

/** WHERE 输入框光标上下文（建议弹层）：当前词元与建议阶段. */
export interface TokenContext {
  /** field=正在输入字段/JSON 路径；value=field: 之后正在输入值 */
  phase: 'field' | 'value';
  /** value 阶段的字段引用（经白名单校验，非法字段无值建议） */
  field?: string;
  /** 字段 kind（非法字段缺省） */
  kind?: FieldKind;
  /** field 阶段的不完整 JSON 括号路径前缀（如 attributes["http"["），建议其子属性 */
  jsonPrefix?: { root: string; segments: (string | number)[] };
  /** 已输入的候选过滤词（field 阶段=字段前缀；value 阶段=值前缀，引号已剥离） */
  typed: string;
  /** 词元在输入串内的替换区间 [start, end) */
  start: number;
  end: number;
}

/** 剥离值前缀的引导引号（短语输入进行中）. */
function stripValueQuote(text: string): string {
  return text.startsWith('"') ? text.slice(1) : text;
}

/** 解析 JSON 括号路径前缀：完整段逐个入 segments，尾随不完整段（["ht 形态）的文本作为过滤词返回；非 JSON 前缀返回 null. */
function parseBracketPrefix(token: string, domain: SearchDomain): { root: string; segments: (string | number)[]; tail: string } | null {
  const root = token.match(/^\w+/)?.[0];
  if (!root || !jsonRoots(domain).includes(root)) {
    return null;
  }

  const suffix = token.slice(root.length);
  if (!suffix.startsWith('[')) {
    return null;
  }

  const segments: (string | number)[] = [];
  let rest = suffix;
  for (;;) {
    const complete = rest.match(/^\[((?:"(?:[^"\\]|\\.)*")|(?:\d+))\]/);
    if (complete) {
      const raw = complete[1];
      if (raw.startsWith('"')) {
        try {
          segments.push(JSON.parse(raw) as string);
        } catch {
          return null;
        }
      } else {
        segments.push(Number(raw));
      }

      rest = rest.slice(complete[0].length);
      continue;
    }

    // 尾随不完整段：[ / [" / ["text —— 其文本作为子属性过滤词
    const partial = rest.match(/^\["?((?:[^"\\]|\\.)*)"?$/);
    if (partial) {
      return { root, segments, tail: partial[1] };
    }

    return null;
  }
}

/**
 * 解析 WHERE 输入框光标处的词元上下文（建议弹层数据源）：
 * 从光标反向扫描词元（空白/括号边界，与 tokenize 词元边界一致），按方括号外首个冒号切分 field/value 阶段.
 * 注意：引号短语进行中（含空格）反向扫描会在空格截断，此时按裸词（字段建议）处理——仅影响输入中途的提示质量，不影响编译.
 */
export function resolveTokenContext(input: string, caret: number, domain: SearchDomain): TokenContext {
  const end = Math.min(Math.max(caret, 0), input.length);
  let start = end;
  while (start > 0 && !/[\s()]/.test(input[start - 1])) {
    start--;
  }

  const token = input.slice(start, end);
  const fail: TokenContext = { phase: 'field', typed: token, start, end };

  // 方括号外首个冒号切分（JSON key 内的冒号不切分，如 attributes["time:stamp"]）
  let depth = 0;
  let colon = -1;
  for (let i = 0; i < token.length; i++) {
    const ch = token[i];
    if (ch === '[') depth++;
    else if (ch === ']') depth = Math.max(0, depth - 1);
    else if (ch === ':' && depth === 0) {
      colon = i;
      break;
    }
  }

  if (colon > 0) {
    let field = token.slice(0, colon);
    // 前导 -（NOT）：剥离后校验字段，值建议照常给出
    if (field.startsWith('-')) {
      field = field.slice(1);
    }

    const kind = fieldKind(field, domain);
    return {
      phase: 'value',
      field,
      kind: kind ?? undefined,
      typed: stripValueQuote(token.slice(colon + 1)),
      start,
      end,
    };
  }

  // 字段阶段：不完整 JSON 括号前缀 → 建议子属性；前导 -（NOT）剥离
  const typed = token.startsWith('-') ? token.slice(1) : token;
  const prefix = parseBracketPrefix(typed, domain);
  if (prefix) {
    return { phase: 'field', jsonPrefix: { root: prefix.root, segments: prefix.segments }, typed: prefix.tail, start, end };
  }

  return { ...fail, typed };
}

/** 单个比较条件 → SQL 片段（kind 决定是否 TRY_CAST 数值语义）. */
function conditionSql(meta: FieldMeta, op: string, rawValue: string, params: SearchSqlParameters): string {
  if (meta.kind === 'number') {
    const value = Number(rawValue);
    if (!Number.isFinite(value)) {
      throw new LuceneCompileError(`字段需要数值：${rawValue}`);
    }

    return `TRY_CAST(${meta.expression} AS DOUBLE) ${op} ${bindParam(params, value)}`;
  }

  if (op !== '=' && op !== '!=') {
    throw new LuceneCompileError('比较操作符（> >= < <=）仅支持数值字段');
  }

  return `${meta.expression} ${op} ${bindParam(params, rawValue)}`;
}

/** 单个比较条件 → 条件节点. */
function buildCondition(meta: FieldMeta, op: string, rawValue: string, params: SearchSqlParameters): Node {
  return { type: 'condition', sql: conditionSql(meta, op, rawValue, params) };
}

/** 词法单元 → 条件节点（field:value / -field:value / 裸词）. */
function buildTerm(token: Token, domain: SearchDomain, params: SearchSqlParameters): Node {
  if (token.kind === 'phrase') {
    return { type: 'condition', sql: fullTextCondition(domain, token.text, params) };
  }

  if (token.text.startsWith('-') && token.text.length > 1) {
    return { type: 'not', child: buildTerm({ kind: 'word', text: token.text.slice(1) }, domain, params) };
  }

  let colon = -1;
  let quoted = false;
  for (let i = 0; i < token.text.length; i++) {
    if (token.text[i] === '\\' && quoted) { i++; continue; }
    if (token.text[i] === '"') quoted = !quoted;
    if (token.text[i] === ':' && !quoted) { colon = i; break; }
  }
  if (colon <= 0) {
    // 裸词：body/名称子串匹配
    return { type: 'condition', sql: fullTextCondition(domain, token.text, params) };
  }

  const field = token.text.slice(0, colon);
  const rawValue = token.text.slice(colon + 1);
  if (rawValue.length === 0) {
    throw new LuceneCompileError(`字段 ${field} 缺少值（如 ${field}:error）`);
  }

  const meta = resolveField(field, domain, params);
  if (rawValue === '*') return { type: 'condition', sql: `${meta.expression} IS NOT NULL` };

  // 引用值两侧的星号由侧栏 contains/prefix 序列化使用；引号里的星号始终是字面值.
  const contains = rawValue.startsWith('*"') && rawValue.endsWith('"*');
  const prefix = rawValue.startsWith('"') && rawValue.endsWith('"*');
  if (contains || prefix) {
    const literal = unquote(rawValue.slice(contains ? 1 : 0, -1));
    return { type: 'condition', sql: buildFilterConditionSql(field, contains ? 'contains' : 'prefix', literal, domain, params) };
  }
  const inQuote = rawValue.startsWith('"') && rawValue.endsWith('"') && rawValue.length >= 2;
  const value = inQuote ? unquote(rawValue) : rawValue;

  // 数值比较：引用值不解释比较符（例如 service:">api" 是字面值）.
  const compareMatch = !inQuote && value.match(/^(>=|<=|>|<)\s*(.+)$/);
  if (compareMatch) {
    return buildCondition(meta, compareMatch[1], compareMatch[2].trim(), params);
  }

  // 前缀匹配：field:pre*（仅字符串语义）
  if (!inQuote && value.endsWith('*')) {
    if (meta.kind === 'number') {
      throw new LuceneCompileError('前缀匹配（*）仅支持字符串字段');
    }

    return { type: 'condition', sql: `starts_with(${meta.expression}, ${bindParam(params, value.slice(0, -1))})` };
  }

  return buildCondition(meta, '=', value, params);
}

/** 裸词/短语全文匹配：logs 匹配 body，traces 匹配 name/service/trace_id（子串，转义通配符）. */
function fullTextCondition(domain: SearchDomain, raw: string, params: SearchSqlParameters): string {
  const param = likeParam(params, raw);
  if (domain === 'logs') {
    return `body LIKE ${param}`;
  }

  if (domain === 'iot') return `(device_key LIKE ${param} OR signal LIKE ${param} OR str_value LIKE ${param})`;
  if (domain === 'metrics') return `name LIKE ${param}`;
  if (domain === 'sessions') return `(session_id LIKE ${param} OR user_agent LIKE ${param} OR domain LIKE ${param})`;
  return `(name LIKE ${param} OR service LIKE ${param} OR trace_id LIKE ${param})`;
}

/** 递归下降解析：expr := andExpr (OR andExpr)*；andExpr := clause+；clause := '(' expr ')' | term. */
function parse(tokens: Token[], domain: SearchDomain, params: SearchSqlParameters): Node | null {
  let pos = 0;

  const peek = () => (pos < tokens.length ? tokens[pos] : null);
  const isOrKeyword = (token: Token) => token.kind === 'word' && /^(OR|or)$/.test(token.text);

  const parseTerm = (): Node => {
    const token = peek();
    if (!token) {
      throw new LuceneCompileError('搜索表达式意外结束');
    }

    pos++;
    if (token.kind === 'word' && /^(NOT|not|-)$/.test(token.text)) {
      return { type: 'not', child: parseTerm() };
    }
    if (token.kind === 'word' && /^(AND|and|OR|or)$/.test(token.text)) throw new LuceneCompileError('布尔运算符缺少条件');
    if (token.kind === 'lparen') {
      const inner = parseOr();
      const closing = peek();
      if (!closing || closing.kind !== 'rparen') {
        throw new LuceneCompileError('括号未闭合');
      }

      pos++;
      return inner;
    }

    if (token.kind === 'rparen') {
      throw new LuceneCompileError('意外的右括号 ")"');
    }

    return buildTerm(token, domain, params);
  };

  const parseAnd = (): Node => {
    const children: Node[] = [];
    let current = peek();
    while (current && current.kind !== 'rparen' && !isOrKeyword(current)) {
      if (current.kind === 'word' && /^(AND|and)$/.test(current.text)) {
        if (children.length === 0) throw new LuceneCompileError('AND 缺少左侧条件');
        pos++;
      }
      children.push(parseTerm());
      current = peek();
    }

    if (children.length === 0) {
      throw new LuceneCompileError('空的搜索子句（括号内需要至少一个条件）');
    }

    return children.length === 1 ? children[0] : { type: 'and', children };
  };

  const parseOr = (): Node => {
    let left = parseAnd();
    let current = peek();
    while (current && isOrKeyword(current)) {
      pos++;
      const right = parseAnd();
      left = { type: 'or', children: [left, right] };
      current = peek();
    }

    return left;
  };

  const root = parseOr();
  if (pos < tokens.length) {
    throw new LuceneCompileError(`无法解析的片段：${tokens[pos].text}`);
  }

  return root;
}

/** AST → WHERE 片段（复合子项显式加括号，不依赖 SQL 运算符优先级）. */
function emit(node: Node): string {
  switch (node.type) {
    case 'condition':
      return node.sql;
    case 'not':
      return `NOT (${emit(node.child)})`;
    case 'and':
      return node.children.map((child) => (child.type === 'or' ? `(${emit(child)})` : emit(child))).join(' AND ');
    case 'or':
      return node.children.map((child) => (child.type === 'and' || child.type === 'or' ? `(${emit(child)})` : emit(child))).join(' OR ');
  }
}

/**
 * 编译 Lucene 风格属性搜索表达式.
 * 空白输入返回空片段（不附加任何条件）.
 * @throws {LuceneCompileError} 语法或字段校验失败（消息人类可读，可直接展示）
 */
export function compileLucene(input: string, domain: SearchDomain, parameters?: SearchSqlParameters): CompiledLucene {
  const text = input.trim();
  if (text.length === 0) {
    return { sql: '', params: {} };
  }

  const params: SearchSqlParameters = parameters ?? {};
  const tokens = tokenize(text);
  const root = parse(tokens, domain, params);
  if (!root) {
    return { sql: '', params: {} };
  }

  return { sql: emit(root), params };
}

/**
 * 过滤器面板操作符（条件构建器模式，与文本表达式共用字段校验与参数绑定）.
 * 过滤器树扩展：in/nin（值列表勾选，组内 OR）、exists/nexists（键存在性）.
 */
export type FilterOp = 'eq' | 'neq' | 'gt' | 'gte' | 'lt' | 'lte' | 'prefix' | 'contains' | 'in' | 'nin' | 'exists' | 'nexists' | 'root';

const OP_TO_SQL: Record<Exclude<FilterOp, 'prefix' | 'contains' | 'in' | 'nin' | 'exists' | 'nexists' | 'root'>, string> = {
  eq: '=',
  neq: '!=',
  gt: '>',
  gte: '>=',
  lt: '<',
  lte: '<=',
};

/** IN/NOT IN 值列表上限（服务端命名参数上限 64，需预留表达式与直方图参数空间）. */
export const IN_MAX_VALUES = 20;

/** Denoise（抑制噪声）固定条件：排除 DEBUG/TRACE 级别（仅 logs 域生效）. */
export const DENOISE_SQL = "severity NOT IN ('DEBUG', 'TRACE')";

/**
 * 过滤器面板单条件 → 参数化 SQL 片段（值绑定 $pN，返回片段；调用方负责并入 WHERE）.
 * @param rawValue 标量操作符传字符串；in/nin 传值列表；exists/nexists 忽略值
 * @throws {LuceneCompileError} 字段校验失败、操作符与字段类型不匹配或 IN 值列表超限
 */
export function buildFilterConditionSql(field: string, op: FilterOp, rawValue: string | string[], domain: SearchDomain, params: SearchSqlParameters): string {
  const meta = resolveField(field, domain, params);

  if (op === 'root') {
    if (domain !== 'traces' || field !== 'parent_span_id') {
      throw new LuceneCompileError('仅根 span 过滤只支持链路父标识');
    }

    return "(parent_span_id IS NULL OR parent_span_id = '' OR parent_span_id = '0000000000000000')";
  }

  if (op === 'exists' || op === 'nexists') {
    if (Array.isArray(rawValue)) {
      throw new LuceneCompileError('存在性过滤不接受值列表');
    }

    return `${meta.expression} ${op === 'exists' ? 'IS NOT NULL' : 'IS NULL'}`;
  }

  if (op === 'in' || op === 'nin') {
    const values = Array.isArray(rawValue) ? rawValue : [rawValue];
    if (values.length === 0) {
      throw new LuceneCompileError('IN 过滤至少需要一个值');
    }

    if (values.length > IN_MAX_VALUES) {
      throw new LuceneCompileError(`IN 过滤值过多（最多 ${IN_MAX_VALUES} 个）`);
    }

    const placeholders = values.map((value) => {
      if (meta.kind === 'number') {
        const num = Number(value);
        if (!Number.isFinite(num)) {
          throw new LuceneCompileError(`字段需要数值：${value}`);
        }

        return bindParam(params, num);
      }

      return bindParam(params, value);
    });
    const left = meta.kind === 'number' ? `TRY_CAST(${meta.expression} AS DOUBLE)` : meta.expression;
    return `${left} ${op === 'in' ? 'IN' : 'NOT IN'} (${placeholders.join(', ')})`;
  }

  if (Array.isArray(rawValue)) {
    throw new LuceneCompileError('该操作符不接受值列表');
  }

  if (op === 'prefix') {
    if (meta.kind === 'number') {
      throw new LuceneCompileError('前缀匹配仅支持字符串字段');
    }

    return `starts_with(${meta.expression}, ${bindParam(params, rawValue)})`;
  }

  if (op === 'contains') {
    return `${meta.expression} LIKE ${likeParam(params, rawValue)}`;
  }

  return conditionSql(meta, OP_TO_SQL[op], rawValue, params);
}
