import { useMemo, useState } from 'react';
import { ChevronDown, ChevronRight, Plus, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { SEARCH_FIELDS } from '@/api/search';
import type { SearchDomain, SearchSqlParameters } from '@/api/types';
import { Button, Checkbox, Input, Popover, Select } from '@/design-system';
import { cn } from '@/utils/cn';
import { buildChildKeysSql, likePattern, useDebounced, useFacetEntries } from './facets';
import { formatJsonField, jsonRoots, parseJsonField, splitFieldList } from './jsonFields';

/** 顶栏 SQL 关键字条（SELECT/ORDER BY 触发器）的统一观感：与 v2 Input 同框高. */
const KEYWORD_TRIGGER_CLS =
  'flex h-8 w-full min-w-0 cursor-pointer items-center gap-2 rounded-md border border-border bg-surface px-2.5 ' +
  'text-left transition-colors hover:border-border-strong focus-visible:outline-2 focus-visible:outline-offset-1 ' +
  'focus-visible:outline-primary aria-expanded:border-primary aria-expanded:ring-2 aria-expanded:ring-primary/20';

interface KeywordBarProps {
  /** SQL 关键字（SELECT / ORDER BY，等宽粗体淡色） */
  keyword: string;
  /** 展示的表达式文本 */
  expression: string;
  title: string;
  testId: string;
  children: React.ReactNode;
}

/** HyperDX 顶栏关键字条：关键字 + 表达式，点击弹出编辑浮层. */
function KeywordBar({ keyword, expression, title, testId, children }: KeywordBarProps) {
  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button type="button" data-testid={testId} title={title} className={KEYWORD_TRIGGER_CLS}>
          <span className="shrink-0 font-mono text-xs font-semibold text-subtle">{keyword}</span>
          <span className="min-w-0 truncate font-mono text-[13px] text-fg">{expression}</span>
          <ChevronDown className="ml-auto size-3 shrink-0 text-subtle" aria-hidden />
        </button>
      </Popover.Trigger>
      <Popover.Content className="w-max max-w-[380px]">{children}</Popover.Content>
    </Popover.Root>
  );
}

interface SelectColumnsPopoverProps {
  domain: SearchDomain;
  /** 显示字段串（逗号分隔），受控 */
  value: string;
  onChange: (value: string) => void;
  /** 发现查询时间窗（最近一次执行，ISO；未就绪为 null——发现树空闲并提示） */
  from: string | null;
  to: string | null;
  denoise: boolean;
}

/** 解析显示字段串（"ts, service" → ['ts','service']；引号内逗号属于 JSON key，走 splitFieldList）. */
function parseFields(value: string): string[] {
  return splitFieldList(value);
}/** 显示列选择浮层：物理列复选 + JSON 属性发现树（attributes/resource/body 逐层展开，点击复选添加子字段列）+ 手填路径补充入口. */
export function SelectColumnsPopover({ domain, value, onChange, from, to, denoise }: SelectColumnsPopoverProps) {
  const { t } = useTranslation();
  const [draftPath, setDraftPath] = useState('');

  const selected = useMemo(() => parseFields(value), [value]);
  const schemaFields = SEARCH_FIELDS[domain].map((meta) => meta.field);
  const extraFields = selected.filter((field) => !schemaFields.includes(field));

  const commit = (next: string[]) => {
    // ts 常驻（HyperDX 式 Timestamp 列），去重保序
    const merged = ['ts', ...next.filter((field) => field !== 'ts')];
    onChange(merged.join(', '));
  };

  const toggle = (field: string, checked: boolean) => {
    if (field === 'ts') {
      return;
    }

    commit(checked ? [...selected, field] : selected.filter((item) => item !== field));
  };

  const addPath = () => {
    const path = draftPath.trim();
    if (!path || selected.includes(path) || !parseJsonField(path, domain)) {
      return;
    }

    commit([...selected, path]);
    setDraftPath('');
  };

  return (
    <KeywordBar keyword="SELECT" expression={value} title={t('search.selectColumns')} testId="search-fields">
      <div className="text-xs font-medium text-muted">{t('search.selectColumns')}</div>
      <div className="mt-2 flex flex-col gap-1.5">
        {SEARCH_FIELDS[domain].map((meta) => (
          <label
            key={meta.field}
            className="flex cursor-pointer select-none items-center gap-2 font-mono text-[13px] text-fg"
          >
            <Checkbox
              checked={selected.includes(meta.field)}
              disabled={meta.field === 'ts'}
              onChange={(event) => toggle(meta.field, event.target.checked)}
              aria-label={meta.field}
            />
            {meta.field}
            {meta.kind === 'json' && <span className="text-[11px] text-faint">json</span>}
          </label>
        ))}
        {extraFields.map((field) => (
          <label key={field} className="flex cursor-pointer select-none items-center gap-2 font-mono text-[13px] text-fg">
            <Checkbox checked onChange={() => commit(selected.filter((item) => item !== field))} aria-label={field} />
            {field}
            <button
              type="button"
              aria-label="remove"
              onClick={() => commit(selected.filter((item) => item !== field))}
              className="ml-auto cursor-pointer text-subtle transition-colors hover:text-error"
            >
              <X className="size-3" aria-hidden />
            </button>
          </label>
        ))}
      </div>
      <div className="mt-3 border-t border-border pt-3">
        <div className="text-xs font-medium text-muted">{t('search.jsonFieldsTitle')}</div>
        <div className="mt-0.5 text-[11px] text-subtle">{from ? t('search.jsonFieldsSampledHint') : t('search.jsonFieldsIdleHint')}</div>
        <div className="mt-1.5 ml-1 flex flex-col border-l border-border pl-1.5">
          {jsonRoots(domain).map((root) => (
            <JsonNodeRow
              key={root}
              root={root}
              segments={[]}
              domain={domain}
              from={from}
              to={to}
              denoise={denoise}
              selected={selected}
              onToggle={toggle}
            />
          ))}
        </div>
      </div>
      <div className="mt-3 flex items-center gap-1 border-t border-border pt-3">
        <Input
          size="sm"
          value={draftPath}
          onChange={(event) => setDraftPath(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') addPath();
          }}
          placeholder={t('search.customJsonPath')}
          className="font-mono text-xs"
        />
        <Button size="sm" variant="secondary" icon={<Plus className="size-3" aria-hidden />} onClick={addPath}>
          {t('common.create')}
        </Button>
      </div>
    </KeywordBar>
  );
}

/** JSON 发现树节点行：chevron 懒加载子属性（服务端 json_keys 抽样枚举）+ 复选添加该字段为结果列（根列除外——其复选在物理列列表）. */
function JsonNodeRow({ root, segments, domain, from, to, denoise, selected, onToggle }: {
  root: string;
  segments: (string | number)[];
  domain: SearchDomain;
  from: string | null;
  to: string | null;
  denoise: boolean;
  selected: string[];
  onToggle: (field: string, checked: boolean) => void;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const term = useDebounced(search);
  const pattern = likePattern(term);
  const field = formatJsonField(root, segments);
  const parameters = useMemo<SearchSqlParameters>(() => {
    const dict: SearchSqlParameters = {};
    if (pattern) dict.key = pattern;
    return dict;
  }, [pattern]);
  const facet = useFacetEntries({
    domain,
    from,
    to,
    denoise,
    enabled: open && from !== null && to !== null,
    buildSql: (limit) => buildChildKeysSql(domain, denoise, root, segments, limit, term, parameters),
    resetKey: `columns:${field}:${term}`,
    parameters,
  });
  const selectable = segments.length > 0;

  return (
    <div className="py-0.5">
      <div className="flex items-center gap-1">
        <button
          type="button"
          data-testid={`search-json-node-${field}`}
          onClick={() => setOpen(!open)}
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-1 rounded-sm py-0.5 text-left font-mono text-[11px] text-muted transition-colors hover:text-fg"
        >
          {open ? <ChevronDown className="size-3 shrink-0" aria-hidden /> : <ChevronRight className="size-3 shrink-0" aria-hidden />}
          <span className="truncate" title={field}>{segments.length > 0 ? field : root}</span>
          {open && <span className="shrink-0 font-mono text-[10px] text-subtle">{`(${facet.entries.length})`}</span>}
        </button>
        {selectable && (
          <Checkbox
            className="size-3"
            checked={selected.includes(field)}
            onChange={(event) => onToggle(field, event.target.checked)}
            aria-label={field}
          />
        )}
      </div>

      {open && (
        <div className="ml-2.5 flex flex-col border-l border-border pl-1.5">
          <Input
            size="sm"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t('search.facetSearchKeys')}
            testId={`search-json-node-search-${field}`}
            className="mt-1 h-6 font-mono text-[11px]"
          />
          {facet.loading && facet.entries.length === 0 && <NodeHint text={t('search.facetLoading')} />}
          {facet.error && <NodeHint text={facet.error} error />}
          {facet.entries.map((entry) => (
            <JsonNodeRow
              key={entry.name}
              root={root}
              segments={[...segments, /^\d+$/.test(entry.name) ? Number(entry.name) : entry.name]}
              domain={domain}
              from={from}
              to={to}
              denoise={denoise}
              selected={selected}
              onToggle={onToggle}
            />
          ))}
          {!facet.loading && !facet.error && facet.entries.length === 0 && (
            <NodeHint text={t(pattern ? 'search.facetNoMatch' : 'search.facetChildKeysEmpty')} />
          )}
          {facet.truncated && (
            <button
              type="button"
              onClick={facet.loadMore}
              className="cursor-pointer py-1 text-left text-[11px] text-subtle transition-colors hover:text-primary"
            >
              {t('search.facetLoadMore')}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/** 发现树占位提示（加载中 / 空数据 / 搜索无匹配 / 出错）. */
function NodeHint({ text, error }: { text: string; error?: boolean }) {
  return <div className={cn('py-0.5 text-[11px]', error ? 'text-error' : 'text-subtle')} title={text}>{text}</div>;
}

interface SortEditorPopoverProps {
  domain: SearchDomain;
  /** 排序表达式（"ts DESC, service ASC"），受控 */
  value: string;
  onChange: (value: string) => void;
}

interface SortRow {
  field: string;
  desc: boolean;
}

/** 解析排序表达式（非法片段忽略）. */
function parseSorts(value: string): SortRow[] {
  return value
    .split(',')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => {
      const match = part.match(/^(\S+)(\s+(asc|desc))?$/i);
      return match
        ? { field: match[1], desc: (match[3] ?? 'asc').toLowerCase() === 'desc' }
        : { field: part, desc: false };
    });
}

/** 排序编辑浮层：行式 [字段][方向][移除]，变更即时回写并触发重查. */
export function SortEditorPopover({ domain, value, onChange }: SortEditorPopoverProps) {
  const { t } = useTranslation();
  const rows = parseSorts(value);
  const fieldOptions = SEARCH_FIELDS[domain].map((meta) => ({ value: meta.field, label: meta.field }));

  const commit = (next: SortRow[]) => {
    // 至少保留一行（服务端/构建校验要求非空排序）
    const safe = next.length > 0 ? next : rows;
    onChange(safe.map((row) => `${row.field} ${row.desc ? 'DESC' : 'ASC'}`).join(', '));
  };

  return (
    <KeywordBar keyword="ORDER BY" expression={value} title={t('search.orderBy')} testId="search-order-by">
      <div className="text-xs font-medium text-muted">{t('search.orderBy')}</div>
      <div className="mt-2 flex flex-col gap-1.5">
        {rows.map((row, index) => (
          <div key={`${row.field}-${index}`} className="flex items-center gap-1">
            <Select
              className="h-7 min-w-[140px] text-xs"
              value={row.field}
              onValueChange={(next) => commit(rows.map((item, i) => (i === index ? { ...item, field: next } : item)))}
              options={fieldOptions}
              aria-label={t('search.sortField')}
            />
            <Select
              className="h-7 w-[92px] shrink-0 text-xs"
              value={row.desc ? 'desc' : 'asc'}
              onValueChange={(next) => commit(rows.map((item, i) => (i === index ? { ...item, desc: next === 'desc' } : item)))}
              options={[
                { value: 'asc', label: 'ASC' },
                { value: 'desc', label: 'DESC' },
              ]}
              aria-label={t('search.direction')}
            />
            <button
              type="button"
              aria-label={t('common.delete')}
              disabled={rows.length <= 1}
              onClick={() => commit(rows.filter((_, i) => i !== index))}
              className="shrink-0 cursor-pointer p-1 text-subtle transition-colors hover:text-error disabled:pointer-events-none disabled:opacity-40"
            >
              <X className="size-3.5" aria-hidden />
            </button>
          </div>
        ))}
      </div>
      <div className="mt-3 border-t border-border pt-3">
        <Button
          size="sm"
          variant="link"
          icon={<Plus className="size-3" aria-hidden />}
          onClick={() => commit([...rows, { field: 'ts', desc: true }])}
        >
          {t('search.addSortField')}
        </Button>
      </div>
    </KeywordBar>
  );
}
