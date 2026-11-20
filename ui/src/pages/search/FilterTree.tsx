import { useMemo, useState, type ReactNode } from 'react';
import { Ban, Braces, ChevronDown, ChevronRight, Plus, RotateCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { SearchDomain, SearchSqlParameters } from '@/api/types';
import { Checkbox, Input, Tooltip } from '@/design-system';
import { cn } from '@/utils/cn';
import { FACET_GROUPS, buildChildKeysSql, buildFieldValuesSql, buildGroupKeysSql, likePattern, useDebounced, useFacetEntries, type FacetEntries, type FacetGroupConfig } from './facets';
import { formatJsonField, parseJsonField } from './jsonFields';
import { applyKeyToggle, applyValueOnly, applyValueToggle, readFieldState, type SearchFilter } from './filters';

interface FilterTreeProps {
  domain: SearchDomain;
  /** 查询时间窗（来自最近一次执行，ISO 字符串；未就绪为 null） */
  from: string | null;
  to: string | null;
  denoise: boolean;
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
}

/**
 * HyperDX 式属性过滤器树：分组（资源属性/日志属性/物理列）→ key 行（计数 + 常显存在性过滤图标）→
 * 值列表（三态复选框：勾选并入 in、半选并入 nin、未选无过滤；行尾常显「仅此值/排除」文字操作）+ 值搜索 + Load more.
 * 操作图标与文字按钮一律常显（含义经 tooltip/文案表达），不随 hover 显形.
 * 数据经 /analytics/search 直查通道聚合（与直方图同构），不随 filters 变化重查.
 */
export function FilterTree({ domain, from, to, denoise, filters, onFiltersChange }: FilterTreeProps) {
  return (
    <div className="mt-1 flex flex-col" data-testid="search-filter-tree">
      {FACET_GROUPS[domain].map((config) => (
        <FacetGroup
          key={config.id}
          config={config}
          domain={domain}
          from={from}
          to={to}
          denoise={denoise}
          filters={filters}
          onFiltersChange={onFiltersChange}
        />
      ))}
    </div>
  );
}

interface FacetGroupProps extends Omit<FilterTreeProps, 'filters' | 'onFiltersChange'> {
  config: FacetGroupConfig;
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
}

/** 分组行：chevron 折叠 + 名称 + {n}（JSON 组 distinct key 数）或 (n)（物理组 distinct 值数，均为抽样估计）；
 * 展开后顶部为服务端搜索框（属性 key / 值 ILIKE $key 参数化过滤，搜即所得不必枚举）；物理列组行尾常显 ⊕/⊘ 存在性过滤图标. */
function FacetGroup({ config, domain, from, to, denoise, filters, onFiltersChange }: FacetGroupProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(config.defaultOpen ?? false);
  const [search, setSearch] = useState('');
  const term = useDebounced(search);
  const pattern = likePattern(term);
  const fieldState = useMemo(() => (config.column ? readFieldState(filters, config.column) : null), [filters, config.column]);
  // 搜索 $key 与 JSON 路径 $jpN 共用一份参数字典（buildSql 绑定 + hook 透传同一对象）
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
    buildSql: (limit) =>
      config.jsonColumn
        ? buildGroupKeysSql(domain, denoise, config.jsonColumn, limit, term)
        : buildFieldValuesSql(domain, denoise, config.column ?? '', limit, term, parameters),
    resetKey: `${config.id}:${term}`,
    parameters,
  });

  return (
    <div className="py-0.5">
      <div className="flex items-center gap-1">
        <button
          type="button"
          data-testid={`search-filter-group-${config.id}`}
          onClick={() => setOpen(!open)}
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-1 rounded-sm py-1 text-left text-xs text-muted transition-colors hover:text-fg"
        >
          {open ? <ChevronDown className="size-3 shrink-0" aria-hidden /> : <ChevronRight className="size-3 shrink-0" aria-hidden />}
          <span className="truncate">{t(config.labelKey)}</span>
          <span className="ml-auto shrink-0 font-mono text-[11px] text-subtle">
            {config.jsonColumn ? `{${facet.entries.length}}` : `(${facet.entries.length})`}
          </span>
        </button>
        {fieldState && (
          <>
            <KeyActionIcon
              active={fieldState.exists}
              activeClass="text-primary"
              label={t('search.facetInclude')}
              onClick={() => onFiltersChange(applyKeyToggle(filters, config.column!, fieldState.exists ? 'none' : 'exists'))}
            >
              <Plus className="size-3" aria-hidden />
            </KeyActionIcon>
            <KeyActionIcon
              active={fieldState.nexists}
              activeClass="text-error"
              label={t('search.facetExclude')}
              onClick={() => onFiltersChange(applyKeyToggle(filters, config.column!, fieldState.nexists ? 'none' : 'nexists'))}
            >
              <Ban className="size-3" aria-hidden />
            </KeyActionIcon>
          </>
        )}
      </div>

      {open && (
        <div className="ml-2.5 flex flex-col border-l border-border pl-1.5">
          <Input
            size="sm"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t(config.jsonColumn ? 'search.facetSearchKeys' : 'search.facetSearchValues')}
            testId={`search-filter-keys-search-${config.id}`}
            className="mt-1 h-6 font-mono text-[11px]"
          />
          {config.jsonColumn ? (
            <>
              {facet.loading && facet.entries.length === 0 && <FacetHint text={t('search.facetLoading')} />}
              {facet.error && <FacetError text={facet.error} onRetry={facet.reload} />}
              {facet.entries.map((entry) => (
                <KeyRow
                  key={entry.name}
                  field={`${config.jsonColumn}.${entry.name}`}
                  name={entry.name}
                  count={entry.count}
                  domain={domain}
                  from={from}
                  to={to}
                  denoise={denoise}
                  filters={filters}
                  onFiltersChange={onFiltersChange}
                />
              ))}
              {!facet.loading && !facet.error && facet.entries.length === 0 && (
                <FacetHint text={t(pattern ? 'search.facetNoMatch' : 'search.facetEmpty')} />
              )}
              {facet.truncated && <LoadMoreButton onClick={facet.loadMore} label={t('search.facetLoadMore')} testId="search-filter-keys-load-more" />}
            </>
          ) : (
            <ValueList
              field={config.column ?? ''}
              facet={facet}
              term={term}
              showSearch={false}
              filters={filters}
              onFiltersChange={onFiltersChange}
            />
          )}
        </div>
      )}
    </div>
  );
}

interface KeyRowProps {
  field: string;
  name: string;
  count: number;
  domain: SearchDomain;
  from: string | null;
  to: string | null;
  denoise: boolean;
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
}

/** key 行：chevron + 名称 + 计数（抽样估计）+ 常显图标（⊕ 键存在 / ⊘ 键不存在 / {} 子属性浏览），chevron 展开值列表（服务端搜索），{} 展开子属性树（逐层懒加载，发现自当前时间窗抽样）. */
function KeyRow({ field, name, count, domain, from, to, denoise, filters, onFiltersChange }: KeyRowProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [childrenOpen, setChildrenOpen] = useState(false);
  const [search, setSearch] = useState('');
  const term = useDebounced(search);
  const pattern = likePattern(term);
  const state = useMemo(() => readFieldState(filters, field), [filters, field]);
  const jsonField = useMemo(() => parseJsonField(field, domain), [field, domain]);
  const parameters = useMemo<SearchSqlParameters>(() => {
    const dict: SearchSqlParameters = {};
    if (pattern) dict.key = pattern;
    return dict;
  }, [pattern]);
  const values = useFacetEntries({
    domain,
    from,
    to,
    denoise,
    enabled: open,
    buildSql: (limit) => buildFieldValuesSql(domain, denoise, field, limit, term, parameters),
    resetKey: `values:${field}:${term}`,
    parameters,
  });

  return (
    <div className="py-0.5">
      <div className="flex items-center gap-1">
        <button
          type="button"
          data-testid={`search-filter-key-${field}`}
          onClick={() => setOpen(!open)}
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-1 rounded-sm py-0.5 text-left text-xs text-muted transition-colors hover:text-fg"
        >
          {open ? <ChevronDown className="size-3 shrink-0" aria-hidden /> : <ChevronRight className="size-3 shrink-0" aria-hidden />}
          <span className="truncate font-mono text-[11px]" title={name}>{name}</span>
          <span className="shrink-0 font-mono text-[11px] text-subtle">{`(${count})`}</span>
        </button>
        {jsonField && (
          <Tooltip content={t('search.facetChildKeys')} side="top">
            <button
              type="button"
              aria-label={t('search.facetChildKeys')}
              data-testid={`search-filter-key-children-${field}`}
              onClick={() => setChildrenOpen(!childrenOpen)}
              className={cn('shrink-0 cursor-pointer p-0.5 transition hover:text-fg', childrenOpen ? 'text-primary' : 'text-subtle')}
            >
              <Braces className="size-3" aria-hidden />
            </button>
          </Tooltip>
        )}
        <KeyActionIcon
          active={state.exists}
          activeClass="text-primary"
          label={t('search.facetInclude')}
          onClick={() => onFiltersChange(applyKeyToggle(filters, field, state.exists ? 'none' : 'exists'))}
        >
          <Plus className="size-3" aria-hidden />
        </KeyActionIcon>
        <KeyActionIcon
          active={state.nexists}
          activeClass="text-error"
          label={t('search.facetExclude')}
          onClick={() => onFiltersChange(applyKeyToggle(filters, field, state.nexists ? 'none' : 'nexists'))}
        >
          <Ban className="size-3" aria-hidden />
        </KeyActionIcon>
      </div>

      {open && (
        <div className="ml-3 pt-0.5">
          <ValueList field={field} facet={values} term={term} search={search} onSearchChange={setSearch} filters={filters} onFiltersChange={onFiltersChange} />
        </div>
      )}
      {childrenOpen && jsonField && (
        <div className="ml-3 pt-0.5">
          <JsonChildKeys
            field={jsonField}
            displayField={field}
            domain={domain}
            from={from}
            to={to}
            denoise={denoise}
            filters={filters}
            onFiltersChange={onFiltersChange}
          />
        </div>
      )}
    </div>
  );
}

/** JSON 节点子属性列表：服务端 json_keys 懒加载（抽样估计），子行递归复用 KeyRow（值分布 + 更深层子属性）. */
function JsonChildKeys({ field, displayField, domain, from, to, denoise, filters, onFiltersChange }: {
  field: { root: string; segments: (string | number)[] };
  /** 父行显示字段（点号 legacy 形态），子行以括号路径展开 */
  displayField: string;
  domain: SearchDomain;
  from: string | null;
  to: string | null;
  denoise: boolean;
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
}) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const term = useDebounced(search);
  const pattern = likePattern(term);
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
    buildSql: (limit) => buildChildKeysSql(domain, denoise, field.root, field.segments, limit, term, parameters),
    resetKey: `children:${displayField}:${term}`,
    parameters,
  });

  return (
    <div className="flex flex-col gap-0.5 pb-1" data-testid={`search-filter-children-${displayField}`}>
      <Input
        size="sm"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        placeholder={t('search.facetSearchKeys')}
        testId={`search-filter-children-search-${displayField}`}
        className="h-6 font-mono text-[11px]"
      />
      {facet.loading && facet.entries.length === 0 && <FacetHint text={t('search.facetLoading')} />}
      {facet.error && <FacetError text={facet.error} onRetry={facet.reload} />}
      {facet.entries.map((entry) => {
        const segments = [...field.segments, /^\d+$/.test(entry.name) ? Number(entry.name) : entry.name];
        return (
          <KeyRow
            key={entry.name}
            field={formatJsonField(field.root, segments)}
            name={entry.name}
            count={entry.count}
            domain={domain}
            from={from}
            to={to}
            denoise={denoise}
            filters={filters}
            onFiltersChange={onFiltersChange}
          />
        );
      })}
      {!facet.loading && !facet.error && facet.entries.length === 0 && (
        <FacetHint text={t(pattern ? 'search.facetNoMatch' : 'search.facetChildKeysEmpty')} />
      )}
      {facet.truncated && <LoadMoreButton onClick={facet.loadMore} label={t('search.facetLoadMore')} testId="search-filter-children-load-more" />}
    </div>
  );
}

/** 存在性过滤图标按钮（常显不随 hover 显形，激活态着色；含义经 aria-label + Tooltip 表达）. */
function KeyActionIcon({ active, activeClass, label, onClick, children }: {
  active: boolean;
  activeClass: string;
  label: string;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <Tooltip content={label} side="top">
      <button
        type="button"
        aria-label={label}
        onClick={onClick}
        className={cn('shrink-0 cursor-pointer p-0.5 transition hover:text-fg', active ? activeClass : 'text-subtle')}
      >
        {children}
      </button>
    </Tooltip>
  );
}

/** 值列表：搜索框（受控，搜索词经防抖触发服务端 ILIKE 重查；物理列组的搜索由组头输入承载，showSearch=false 避免双框）+ 三态复选框行 + Load more（列表由 hook 服务端过滤）. */
function ValueList({ field, facet, term, showSearch = true, search = '', onSearchChange, filters, onFiltersChange }: {
  field: string;
  facet: FacetEntries;
  /** 防抖后的生效搜索词（空态提示据此区分「无匹配」/「无数据」） */
  term: string;
  /** 是否渲染搜索框（key 行值列表 true；物理列组由组头搜索承载，false） */
  showSearch?: boolean;
  search?: string;
  onSearchChange?: (value: string) => void;
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
}) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col gap-0.5 pb-1" data-testid={`search-filter-values-${field}`}>
      {showSearch && (
        <Input
          size="sm"
          value={search}
          onChange={(event) => onSearchChange?.(event.target.value)}
          placeholder={t('search.facetSearchValues')}
          testId={`search-filter-values-search-${field}`}
          className="h-6 font-mono text-[11px]"
        />
      )}
      {facet.loading && facet.entries.length === 0 && <FacetHint text={t('search.facetLoading')} />}
      {facet.error && <FacetError text={facet.error} onRetry={facet.reload} />}
      {facet.entries.map((entry) => (
        <ValueRow key={entry.name} field={field} entry={entry} filters={filters} onFiltersChange={onFiltersChange} />
      ))}
      {!facet.loading && !facet.error && facet.entries.length > 0 && facet.truncated && (
        <LoadMoreButton onClick={facet.loadMore} label={t('search.facetLoadMore')} testId="search-filter-load-more" />
      )}
      {!facet.loading && !facet.error && facet.entries.length === 0 && (
        <FacetHint text={t(term.trim() ? 'search.facetNoMatch' : 'search.facetEmpty')} />
      )}
    </div>
  );
}

/**
 * 值行三态复选框：勾选=并入 in 过滤（组内 OR），半选=并入 nin（删除线），未选=无过滤；
 * 行尾常显文字操作（HyperDX 式，含义自明无需猜图标）：「仅此值」=清空该字段仅留此值，「排除/取消排除」=切换 nin.
 */
function ValueRow({ field, entry, filters, onFiltersChange }: {
  field: string;
  entry: { name: string; count: number };
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
}) {
  const { t } = useTranslation();
  const state = useMemo(() => readFieldState(filters, field), [filters, field]);
  const included = state.included.has(entry.name);
  const excluded = state.excluded.has(entry.name);

  return (
    <div className="flex items-center gap-1 rounded-sm px-0.5 py-0.5 hover:bg-surface-2" data-testid={`search-filter-value-${entry.name}`}>
      <Checkbox
        className="size-3"
        checked={included}
        indeterminate={excluded}
        aria-label={entry.name}
        onChange={() => onFiltersChange(applyValueToggle(filters, field, entry.name, included ? 'none' : 'include'))}
      />
      <span
        className={cn(
          'min-w-0 flex-1 truncate font-mono text-[11px]',
          excluded ? 'text-subtle line-through' : included ? 'text-fg' : 'text-muted',
        )}
        title={entry.name}
      >
        {entry.name}
      </span>
      <span className="shrink-0 font-mono text-[10px] text-subtle">{entry.count}</span>
      <button
        type="button"
        data-testid={`search-filter-value-only-${entry.name}`}
        onClick={() => onFiltersChange(applyValueOnly(filters, field, entry.name))}
        className="shrink-0 cursor-pointer text-[11px] text-subtle transition-colors hover:text-primary"
      >
        {t('search.facetValueOnly')}
      </button>
      <button
        type="button"
        data-testid={`search-filter-value-exclude-${entry.name}`}
        onClick={() => onFiltersChange(applyValueToggle(filters, field, entry.name, excluded ? 'none' : 'exclude'))}
        className={cn(
          'shrink-0 cursor-pointer text-[11px] transition-colors',
          excluded ? 'text-error hover:text-fg' : 'text-subtle hover:text-error',
        )}
      >
        {excluded ? t('search.facetRemoveExclude') : t('search.facetExcludeValue')}
      </button>
    </div>
  );
}

/** Load more 按钮（提高 LIMIT 重查，非分页）. */
function LoadMoreButton({ onClick, label, testId }: { onClick: () => void; label: string; testId: string }) {
  return (
    <button
      type="button"
      data-testid={testId}
      onClick={onClick}
      className="cursor-pointer py-1 text-left text-[11px] text-subtle transition-colors hover:text-primary"
    >
      <ChevronRight className="mr-0.5 inline size-3" aria-hidden />
      {label}
    </button>
  );
}

/** facet 占位提示（加载中 / 空数据 / 搜索无匹配）. */
function FacetHint({ text }: { text: string }) {
  return <div className="py-0.5 text-[11px] text-subtle">{text}</div>;
}

/** facet 加载失败（人类可读错误 + 重试）. */
function FacetError({ text, onRetry }: { text: string; onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center gap-1 py-0.5 text-[11px] text-error">
      <span className="min-w-0 flex-1 truncate" title={text}>{text}</span>
      <Tooltip content={t('search.facetRetry')} side="top">
        <button type="button" aria-label={t('search.facetRetry')} onClick={onRetry} className="cursor-pointer p-0.5 transition hover:text-fg">
          <RotateCw className="size-3" aria-hidden />
        </button>
      </Tooltip>
    </div>
  );
}
