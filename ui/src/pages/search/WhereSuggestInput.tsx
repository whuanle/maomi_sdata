import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SEARCH_FIELDS } from '@/api/search';
import type { SearchSqlParameters } from '@/api/types';
import type { ChartSource as SearchDomain } from '@/api/chartTypes';
import { Button, Input, Popover } from '@/design-system';
import { RotateCw } from 'lucide-react';
import { cn } from '@/utils/cn';
import { buildChildKeysSql, buildFieldValuesSql, likePattern, useDebounced, useFacetEntries } from './facets';
import { formatJsonField, jsonRoots, parseJsonField } from './jsonFields';
import { resolveTokenContext } from './lucene';

/** 单条建议：insert 为采纳后替换当前词元的完整文本，label/hint 供列表展示. */
interface Suggestion {
  insert: string;
  label: string;
  hint?: string;
}

interface WhereSuggestInputProps {
  domain: SearchDomain;
  syntax: 'lucene' | 'sql';
  value: string;
  onChange: (value: string) => void;
  /** Enter 且无弹层时执行查询（有弹层时 Enter/Tab = 采纳候选） */
  onRun: () => void;
  testId: string;
  placeholder?: string;
  ariaLabel?: string;
  fieldsOnly?: boolean;
  className?: string;
  /** 值/子属性建议的查询时间窗（ISO；null 时仅提供静态字段建议） */
  from: string | null;
  to: string | null;
  denoise: boolean;
}

/** SQL 模式静态关键字候选（不做值建议：任意表达式场景，界面不宣称智能值补全）. */
const SQL_KEYWORDS = ['AND', 'OR', 'NOT', 'LIKE', 'IN', 'IS NULL', 'IS NOT NULL'];

const SUGGEST_LIMIT = 10;

/** 字段 kind 的展示提示. */
function kindHint(kind: string | undefined, t: (key: string) => string): string | undefined {
  switch (kind) {
    case 'string':
      return t('search.suggestKindString');
    case 'number':
      return t('search.suggestKindNumber');
    case 'time':
      return t('search.suggestKindTime');
    case 'json':
      return t('search.suggestKindJson');
    default:
      return undefined;
  }
}

/**
 * WHERE 输入框智能建议（HyperDX 式 combobox）：
 * Lucene 模式——裸词建议物理列/JSON 根，`field:` 后建议该字段 top-N 值（数值字段附加比较操作符），
 * JSON 括号路径前缀（attributes["http"["）建议下一层子属性（服务端 json_keys，与 facet 同抽样口径）；
 * SQL 模式——物理列名 + 常用关键字静态候选。
 * 键盘：↑/↓ 高亮、Enter/Tab 采纳（弹层打开时截断运行）、Esc 关闭；IME 组合输入期间不做任何拦截.
 */
export function WhereSuggestInput({ domain, syntax, value, onChange, onRun, testId, placeholder, ariaLabel, fieldsOnly = false, className, from, to, denoise }: WhereSuggestInputProps) {
  const { t } = useTranslation();
  const inputRef = useRef<HTMLInputElement>(null);
  const [caret, setCaret] = useState(0);
  const [focused, setFocused] = useState(false);
  const [active, setActive] = useState(0);
  /** 采纳后待恢复的光标位置（受控 value 提交到 DOM 后 setSelectionRange） */
  const pendingCaret = useRef<number | null>(null);
  const debouncedTyped = useDebounced(value, 200);

  const context = useMemo(() => resolveTokenContext(value, caret, domain), [value, caret, domain]);
  // 值/子属性查询的搜索词按防抖推进（typing 时连续重查），上下文区间仍用实时 caret
  const contextForQuery = useMemo(() => resolveTokenContext(debouncedTyped, debouncedTyped.length, domain), [debouncedTyped, domain]);
  const queryPrefix = contextForQuery.phase === 'field' ? contextForQuery.jsonPrefix : null;
  const baseRef = queryPrefix ? formatJsonField(queryPrefix.root, queryPrefix.segments) : '';

  // JSON 子属性候选查询（服务端 ILIKE 过滤，抽样口径与过滤器树一致）
  const childParams = useMemo<SearchSqlParameters>(() => {
    const dict: SearchSqlParameters = {};
    const pattern = likePattern(contextForQuery.phase === 'field' ? contextForQuery.typed : '');
    if (pattern) dict.key = pattern;
    return dict;
  }, [contextForQuery]);
  const childKeys = useFacetEntries({
    domain,
    from,
    to,
    denoise,
    enabled: syntax === 'lucene' && focused && queryPrefix !== null && from !== null && to !== null,
    buildSql: (limit) => (queryPrefix ? buildChildKeysSql(domain, denoise, queryPrefix.root, queryPrefix.segments, limit, contextForQuery.typed, childParams) : ''),
    resetKey: `suggest:${baseRef}:${contextForQuery.typed}`,
    parameters: childParams,
  });

  // 值候选查询（field: 之后的 top-N 值分布，搜索词 contains 语义）
  const valueField = contextForQuery.phase === 'value' ? contextForQuery.field : undefined;
  const valueParams = useMemo<SearchSqlParameters>(() => {
    const dict: SearchSqlParameters = {};
    const pattern = likePattern(contextForQuery.typed);
    if (pattern) dict.key = pattern;
    return dict;
  }, [contextForQuery]);
  const valueEntries = useFacetEntries({
    domain,
    from,
    to,
    denoise,
    enabled: syntax === 'lucene' && focused && contextForQuery.phase === 'value' && !!valueField && from !== null && to !== null,
    buildSql: (limit) => (valueField ? buildFieldValuesSql(domain, denoise, valueField, limit, contextForQuery.typed, valueParams) : ''),
    resetKey: `suggest-values:${valueField ?? ''}:${contextForQuery.typed}`,
    parameters: valueParams,
  });

  const suggestions = useMemo<Suggestion[]>(() => {
    const typedLower = context.typed.toLowerCase();
    if (syntax === 'sql') {
      // SQL 模式：静态候选（关键字 + 物理列），当前词元前缀过滤（空词元 = 全部候选）
      const keywords = (fieldsOnly ? [] : SQL_KEYWORDS)
        .filter((keyword) => keyword.toLowerCase().startsWith(typedLower))
        .map((keyword) => ({ insert: `${keyword} `, label: keyword }));
      const fields = SEARCH_FIELDS[domain]
        .filter((meta) => meta.field.toLowerCase().includes(typedLower))
        .map((meta) => ({ insert: `${meta.field} `, label: meta.field, hint: kindHint(meta.kind, t) }));
      return [...keywords, ...fields].slice(0, SUGGEST_LIMIT);
    }

    if (context.phase === 'value') {
      if (!context.field) {
        return [];
      }

      // 值含空白时以短语形态插入（tokenize 引号模式保词），采纳后补空格衔接下一子句
      const insertValue = (name: string) => (/\s/.test(name) ? `"${name}"` : name);
      const values = valueEntries.entries.map((entry) => ({
        insert: `${context.field}:${insertValue(entry.name)} `,
        label: entry.name,
        hint: String(entry.count),
      }));
      // 数值比较操作符仅在值前缀为空时展示（开始输入数值后不再占位）
      if (context.kind === 'number' && context.typed.length === 0) {
        const operators = ['>=', '>', '<=', '<'].map((op) => ({
          insert: `${context.field}:${op}`,
          label: op,
          hint: t('search.suggestOperators'),
        }));
        return [...operators, ...values];
      }

      return values;
    }

    if (context.jsonPrefix) {
      if (value !== debouncedTyped) return [];
      // 子属性采纳后提供值筛选/继续下钻两个入口，路径保持 JSON 字符串键语义。
      return childKeys.entries
        .filter((entry) => entry.name.toLowerCase().includes(typedLower))
        .map((entry) => {
          const segment = entry.name;
          return {
            insert: `${baseRef}[${JSON.stringify(segment)}]`,
            label: entry.name,
            hint: String(entry.count),
          };
        });
    }

    const complete = parseJsonField(context.typed, domain);
    if (complete) return [
      { insert: context.typed + ':', label: t('search.suggestFilterValue'), hint: ':' },
      { insert: context.typed + '[', label: t('search.suggestChildKeys'), hint: '[' },
    ];

    // 裸词：JSON 根 + 物理列（ts 时间字段由时间选择器承载，不进属性建议）
    const roots = jsonRoots(domain)
      .filter((root) => root.toLowerCase().startsWith(typedLower))
      .map((root) => ({ insert: `${root}[`, label: root, hint: t('search.suggestKindJson') }));
    const fields = SEARCH_FIELDS[domain]
      .filter((meta) => meta.field !== 'ts' && meta.field.toLowerCase().includes(typedLower))
      .map((meta) => ({ insert: `${meta.field}:`, label: meta.field, hint: kindHint(meta.kind, t) }));
    return [...roots, ...fields];
  }, [context, syntax, domain, t, valueEntries.entries, childKeys.entries, baseRef, fieldsOnly, value, debouncedTyped]);

  const dynamic = syntax === 'lucene' && (!!context.jsonPrefix || context.phase === 'value');
  const pending = dynamic && value !== debouncedTyped;
  const loading = dynamic && (pending || (context.jsonPrefix ? childKeys.loading : valueEntries.loading));
  const suggestionError = dynamic && !pending ? (context.jsonPrefix ? childKeys.error : valueEntries.error) : null;
  const open = focused && (suggestions.length > 0 || dynamic);

  // 候选列表变化时高亮复位
  useEffect(() => {
    setActive(0);
  }, [suggestions.length, context.start]);

  // 受控 value 提交后恢复采纳时的光标位置
  useEffect(() => {
    if (pendingCaret.current !== null && inputRef.current) {
      const position = pendingCaret.current;
      pendingCaret.current = null;
      inputRef.current.setSelectionRange(position, position);
    }
  }, [value]);

  const syncCaret = (element: HTMLInputElement) => {
    setCaret(element.selectionStart ?? element.value.length);
  };

  const adopt = (suggestion: Suggestion) => {
    const next = value.slice(0, context.start) + suggestion.insert + value.slice(context.end);
    pendingCaret.current = context.start + suggestion.insert.length;
    setCaret(pendingCaret.current);
    onChange(next);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    // IME 组合输入期间不拦截任何键（Enter 确认候选词/方向键选词都应交给输入法）
    if (event.nativeEvent.isComposing) {
      return;
    }

    if (open && event.key === 'Escape') {
      event.preventDefault();
      setFocused(false);
      return;
    }

    if (open && suggestions.length > 0) {
      if (event.key === 'ArrowDown') {
        event.preventDefault();
        setActive((index) => (index + 1) % suggestions.length);
        return;
      }

      if (event.key === 'ArrowUp') {
        event.preventDefault();
        setActive((index) => (index - 1 + suggestions.length) % suggestions.length);
        return;
      }

      if (event.key === 'Enter' || event.key === 'Tab') {
        event.preventDefault();
        adopt(suggestions[Math.min(active, suggestions.length - 1)]);
        return;
      }

    }

    if (event.key === 'Enter') {
      onRun();
    }
  };

  return (
    <Popover.Root open={open}>
      <Popover.Anchor asChild>
        <Input
          ref={inputRef}
          value={value}
          onChange={(event) => {
            onChange(event.target.value);
            syncCaret(event.target);
          }}
          onKeyDown={handleKeyDown}
          onFocus={(event) => {
            setFocused(true);
            syncCaret(event.target);
          }}
          onBlur={() => setFocused(false)}
          onSelect={(event) => syncCaret(event.currentTarget)}
          onClick={(event) => syncCaret(event.currentTarget)}
          className={className}
          placeholder={placeholder}
          testId={testId}
          role="combobox"
          aria-expanded={open}
          aria-autocomplete="list"
          aria-label={ariaLabel}
        />
      </Popover.Anchor>
      <Popover.Content
        side="bottom"
        align="start"
        onOpenAutoFocus={(event) => event.preventDefault()}
        className="max-w-[640px] min-w-[var(--radix-popover-anchor-width)] p-1"
      >
        <div className="flex max-h-[280px] flex-col overflow-auto" data-testid={`${testId}-suggest`}>
          {suggestions.map((suggestion, index) => (
            <button
              key={`${suggestion.label}:${index}`}
              type="button"
              role="option"
              aria-selected={index === active}
              data-active={index === active || undefined}
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => adopt(suggestion)}
              className={cn(
                'flex w-full cursor-pointer items-center gap-2 rounded-sm px-2 py-1 text-left font-mono text-[13px] transition-colors hover:bg-surface-2',
                index === active && 'bg-primary-soft',
              )}
            >
              <span className="min-w-0 flex-1 truncate text-fg">{suggestion.label}</span>
              {suggestion.hint && <span className="shrink-0 text-[11px] text-subtle">{suggestion.hint}</span>}
            </button>
          ))}
          {dynamic && (!from || !to) ? <p role="status" className="px-2 py-1 text-xs text-muted">{t('search.suggestNeedsRange')}</p> : loading ? <p role="status" className="px-2 py-1 text-xs text-muted">{t('search.facetLoading')}</p> : suggestionError ? <div role="alert" className="px-2 py-1 text-xs text-error">
            {t('search.suggestFailed')} {suggestionError}
            <Button variant="link" size="sm" icon={<RotateCw className="size-3.5" />} onMouseDown={(event) => event.preventDefault()} onClick={() => context.jsonPrefix ? childKeys.reload() : valueEntries.reload()}>{t('search.suggestRetry')}</Button>
          </div> : dynamic && suggestions.length === 0 && <p role="status" className="px-2 py-1 text-xs text-muted">{t(context.jsonPrefix ? 'search.facetChildKeysEmpty' : 'search.facetEmpty')}</p>}
        </div>
      </Popover.Content>
    </Popover.Root>
  );
}
