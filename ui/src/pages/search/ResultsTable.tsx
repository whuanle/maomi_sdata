import { Fragment, useEffect, useRef, useState } from 'react';
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, Copy, Filter } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, DropdownMenu, SpinArea, toast, Tooltip } from '@/design-system';
import { cn } from '@/utils/cn';
import { formatDateTimeMs, formatDurationUs } from '@/utils/format';
import { SEVERITY_TEXT_CLS, renderDetailValue } from './detailFormat';
import { JsonTree } from './JsonTree';
import { SpanPreview } from './SpanPreview';

/** 行内展开预览模式（field 字段平铺 / json 折叠树 / body 原文）. */
type PreviewMode = 'field' | 'json' | 'body';

const PREVIEW_MODES: PreviewMode[] = ['field', 'json', 'body'];

/** body 原文提取（日志取 body；span 回退 name；其余原样字符串化）. */
function rawBodyText(row: Record<string, unknown>): string {
  const raw = row.body ?? row.name;
  if (raw === null || raw === undefined) {
    return '-';
  }

  return typeof raw === 'string' ? raw : String(raw);
}

/** 固定宽度列（table-fixed 布局锚点）；未列出的列（body/name/JSON 等）均分剩余宽度. */
const COLUMN_WIDTHS: Record<string, number> = {
  ts: 200,
  severity: 96,
  status: 84,
  kind: 64,
  level: 64,
  duration_us: 100,
  trace_id: 110,
  span_id: 120,
  parent_span_id: 120,
  service: 132,
  source: 88,
};

/** 表头列菜单（排序重查 + 添加为过滤器）. */
function ColumnMenu({ name, onSort, onAddFilter }: { name: string; onSort: (field: string, desc: boolean) => void; onAddFilter: (field: string) => void }) {
  const { t } = useTranslation();
  const items: { icon: React.ReactNode; label: string; onSelect: () => void }[] = [
    { icon: <ArrowUp />, label: t('search.sortAsc'), onSelect: () => onSort(name, false) },
    { icon: <ArrowDown />, label: t('search.sortDesc'), onSelect: () => onSort(name, true) },
    { icon: <Filter />, label: t('search.addFilterField'), onSelect: () => onAddFilter(name) },
  ];
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <span
          role="button"
          tabIndex={0}
          className="inline-flex max-w-full cursor-pointer items-center gap-0.5 overflow-hidden outline-none hover:text-fg"
          title={name}
          onClick={(event) => event.stopPropagation()}
          onKeyDown={(event) => {
            if (event.key === 'Enter') event.stopPropagation();
          }}
        >
          <span className="truncate">{name === 'ts' ? t('search.timestampLocal') : name}</span>
          <ChevronDown className="size-2.5 shrink-0" aria-hidden />
        </span>
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="start">
        {items.map((item) => (
          <DropdownMenu.Item key={item.label} icon={item.icon} onSelect={item.onSelect}>
            {item.label}
          </DropdownMenu.Item>
        ))}
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  );
}

export interface ResultsTableProps {
  domain?: 'logs' | 'traces';
  columns: string[];
  /** 对象化行（列名 → 单元格值） */
  rows: Record<string, unknown>[];
  loading?: boolean;
  queryGeneration?: number;
  hasMore?: boolean;
  loadingMore?: boolean;
  loadMoreError?: string | null;
  onLoadMore?: () => void;
  complete?: boolean;
  /** 空态内容（rows 为空时整表替换） */
  empty?: React.ReactNode;
  /** 列头菜单：排序（触发重查） */
  onSort?: (field: string, desc: boolean) => void;
  /** 列头菜单：按字段添加过滤器（空值占位） */
  onAddFilter?: (field: string) => void;
  /** trace_id 点击 → 瀑布图 */
  onTraceOpen: (traceId: string) => void;
  /** 行点击 → 详情抽屉（ClickStack 式侧栏；与行首箭头的就地展开区分） */
  onRowOpen: (row: Record<string, unknown>) => void;
  /** 展开明细里「按此值过滤」 */
  onFilterByValue: (field: string, value: unknown) => void;
}

/**
 * HyperDX 式结果表：table-fixed 高密度单行（触底自动追加续页）、
 * 行首箭头就地展开全字段明细（可按值过滤）、行点击打开详情抽屉、
 * 悬浮复制整行 JSON、severity 着色文本、trace_id 可点击。
 */
export function ResultsTable({ domain = 'logs', columns, rows, loading, queryGeneration, hasMore, loadingMore, loadMoreError, onLoadMore, complete, empty, onSort, onAddFilter, onTraceOpen, onRowOpen, onFilterByValue }: ResultsTableProps) {
  const { t } = useTranslation();
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null);
  const [previewMode, setPreviewMode] = useState<PreviewMode>('field');

  const scrollRef = useRef<HTMLDivElement>(null);
  const sentinelRef = useRef<HTMLDivElement>(null);

  // 新查询复位；追加续页保留滚动位置和展开行。
  useEffect(() => {
    setExpandedIndex(null);
    setPreviewMode('field');
    if (scrollRef.current) scrollRef.current.scrollTop = 0;
  }, [queryGeneration]);

  useEffect(() => {
    const sentinel = sentinelRef.current;
    if (!sentinel || !hasMore || loading || loadingMore || loadMoreError || !onLoadMore || typeof IntersectionObserver === 'undefined') return;
    let active = true;
    const observer = new IntersectionObserver((entries) => {
      if (active && entries.some((entry) => entry.isIntersecting)) onLoadMore();
    }, { root: scrollRef.current, rootMargin: '0px 0px 160px 0px' });
    observer.observe(sentinel);
    return () => { active = false; observer.disconnect(); };
  }, [hasMore, loading, loadingMore, loadMoreError, onLoadMore, rows.length]);

  const copyRowJson = (row: Record<string, unknown>) => {
    try {
      void navigator.clipboard?.writeText(JSON.stringify(row, null, 2));
      toast.success(t('search.copied'));
    } catch {
      // 剪贴板不可用时静默（如非安全上下文）
    }
  };

  if (rows.length === 0 && !loading) {
    return <>{empty}</>;
  }

  return (
    <div className="relative flex h-full min-h-0 flex-col" data-testid="search-results-table">
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto" data-testid="search-results-scroll">
        {/* table-fixed：固定列宽 + 弹性列均分剩余宽度，单元格 truncate 保证单行不破版 */}
        <table className={cn('w-full table-fixed border-separate border-spacing-0 text-[13px]', domain === 'traces' && 'min-w-[1100px]')}>
          <thead>
            <tr>
              <th scope="col" className="sticky top-0 z-[1] w-7 border-b border-border bg-bg" aria-label="expand" />
              {columns.map((name, index) => (
                <th
                  key={name}
                  scope="col"
                  style={COLUMN_WIDTHS[name] !== undefined ? { width: COLUMN_WIDTHS[name] } : undefined}
                  className={cn(
                    'sticky top-0 z-[1] border-b border-border bg-bg px-2 py-1.5 text-left text-xs font-medium whitespace-nowrap text-subtle',
                    index > 0 && 'border-l border-border/50',
                  )}
                >
                  {onSort && onAddFilter ? <ColumnMenu name={name} onSort={onSort} onAddFilter={onAddFilter} /> : <span>{name === 'ts' ? t('search.timestampLocal') : name}</span>}
                </th>
              ))}
              <th scope="col" className="sticky top-0 z-[1] w-8 border-b border-border bg-bg" aria-label="row actions" />
            </tr>
          </thead>
          <tbody className="font-mono">
            {rows.map((row, rowIndex) => {
              const expanded = expandedIndex === rowIndex;
              return (
                <Fragment key={rowIndex}>
                  <tr
                    className={cn('group cursor-pointer transition-colors hover:bg-surface-2/60', expanded && 'bg-surface-2/40')}
                    onClick={() => onRowOpen(row)}
                  >
                    <td className="border-b border-border/40 px-1 py-1 text-center align-middle text-subtle">
                      {/* 行首箭头：就地展开明细（stopPropagation 与行点击开抽屉区分） */}
                      <button
                        type="button"
                        aria-label={t('search.detail')}
                        className="mx-auto flex cursor-pointer rounded-sm p-0.5 outline-none hover:text-fg focus-visible:ring-2 focus-visible:ring-primary/30"
                        onClick={(event) => {
                          event.stopPropagation();
                          setExpandedIndex(expanded ? null : rowIndex);
                        }}
                      >
                        <ChevronRight className={cn('size-3.5 transition-transform', expanded && 'rotate-90 text-fg')} aria-hidden />
                      </button>
                    </td>
                    {columns.map((name) => (
                      <td
                        key={name}
                        style={COLUMN_WIDTHS[name] !== undefined ? { width: COLUMN_WIDTHS[name] } : undefined}
                        className="truncate border-b border-border/40 px-2 py-1 align-middle text-fg"
                      >
                        {renderCell(name, row[name], onTraceOpen, () => onRowOpen(row))}
                      </td>
                    ))}
                    <td className="border-b border-border/40 px-1 py-1 text-center align-middle">
                      <Tooltip content={t('search.copyRowJson')}>
                        <button
                          type="button"
                          aria-label={t('search.copyRowJson')}
                          className="cursor-pointer p-0.5 text-subtle opacity-0 transition-opacity hover:text-fg group-hover:opacity-100"
                          onClick={(event) => {
                            event.stopPropagation();
                            copyRowJson(row);
                          }}
                        >
                          <Copy className="size-3.5" aria-hidden />
                        </button>
                      </Tooltip>
                    </td>
                  </tr>
                  {expanded && (
                    <tr>
                      <td colSpan={columns.length + 2} className="border-b border-border/60 bg-surface-2/40 px-4 py-3">
                        {domain === 'traces' ? <SpanPreview row={row} onFilterByValue={onFilterByValue} onTraceActivate={onTraceOpen} /> : <>
                        {/* 预览模式切换（ClickStack 式：字段平铺 / JSON 折叠树 / 原文） */}
                        <div className="mb-2 flex items-center gap-4 border-b border-border/40 pb-1">
                          {PREVIEW_MODES.map((mode) => (
                            <button
                              key={mode}
                              type="button"
                              className={cn(
                                'cursor-pointer border-b pb-0.5 text-xs transition-colors',
                                previewMode === mode ? 'border-primary font-medium text-fg' : 'border-transparent text-subtle hover:text-fg',
                              )}
                              onClick={() => setPreviewMode(mode)}
                            >
                              {mode === 'field' ? t('search.previewField') : mode === 'json' ? t('search.previewJson') : t('search.previewBody')}
                            </button>
                          ))}
                        </div>
                        {previewMode === 'field' ? (
                          <div className="grid grid-cols-[150px_minmax(0,1fr)] gap-x-3 gap-y-2">
                            {columns.map((name) => (
                              <Fragment key={name}>
                                <div className="pt-0.5 font-sans text-xs text-subtle">{name}</div>
                                <div className="flex min-w-0 items-start gap-1">
                                  <pre className="min-w-0 flex-1 font-mono text-xs break-all whitespace-pre-wrap text-fg">
                                    {renderDetailValue(name, row[name])}
                                  </pre>
                                  {name !== 'ts' && row[name] !== null && row[name] !== undefined && String(row[name]).length > 0 && (
                                    <Tooltip content={t('search.filterByValue')}>
                                      <button
                                        type="button"
                                        aria-label={t('search.filterByValue')}
                                        className="shrink-0 cursor-pointer p-0.5 text-subtle transition-colors hover:text-primary"
                                        onClick={(event) => {
                                          event.stopPropagation();
                                          onFilterByValue(name, row[name]);
                                        }}
                                      >
                                        <Filter className="size-3.5" aria-hidden />
                                      </button>
                                    </Tooltip>
                                  )}
                                </div>
                              </Fragment>
                            ))}
                          </div>
                        ) : previewMode === 'json' ? (
                          /* JSON 折叠树：整行对象递归展开，节点可点击收起/展开 */
                          <div className="rounded-md border border-border bg-bg px-2.5 py-2">
                            <JsonTree value={row} defaultExpanded />
                          </div>
                        ) : (
                          /* 原文模式：body 不做任何解析直接展示 */
                          <pre className="font-mono text-xs break-all whitespace-pre-wrap text-fg">{rawBodyText(row)}</pre>
                        )}
                        </>}
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
        <div ref={sentinelRef} className="h-px" data-testid="search-results-sentinel" />
        {loadingMore && <div role="status" className="py-4 text-center text-xs text-muted">{t('common.loading')}</div>}
        {loadMoreError && (
          <div className="flex items-center justify-center gap-2 py-4 text-xs text-error">
            <span>{loadMoreError}</span>
            <Button variant="link" size="sm" onClick={onLoadMore}><ArrowDown className="size-3" />{t('search.retry')}</Button>
          </div>
        )}
        {!loading && !loadingMore && !loadMoreError && complete && (
          <div role="status" className="rounded-sm bg-surface-2 px-3 py-4 text-center font-mono text-xs text-muted">
            {t('search.endOfResults')}
          </div>
        )}
      </div>
      {loading && (
        <div className="absolute inset-0 z-[2] grid place-items-center bg-[var(--c-bg)]/60">
          <SpinArea />
        </div>
      )}
    </div>
  );
}

/** 单元格语义渲染：时间/级别/状态/耗时/trace_id 特化，其余单行截断（JSON 压平）. */
function renderCell(name: string, value: unknown, onTraceOpen: (traceId: string) => void, onSpanOpen: () => void): React.ReactNode {
  if (value === null || value === undefined) {
    return <span className="text-faint">-</span>;
  }

  switch (name) {
    case 'ts':
      return <span className="whitespace-nowrap" title={String(value)}>{formatDateTimeMs(String(value))}</span>;
    case 'severity':
    case 'status': {
      const text = String(value);
      return (
        <span className={cn('whitespace-nowrap', SEVERITY_TEXT_CLS[text] ?? 'text-fg')} title={text}>
          {text}
        </span>
      );
    }
    case 'duration_us':
      return <span className="tabular-nums" title={`${value}µs`}>{formatDurationUs(Number(value))}</span>;
    case 'trace_id':
    case 'span_id':
      return typeof value === 'string' && value.length > 0 ? (
        <span
          role="button"
          tabIndex={0}
          title={value}
          className="cursor-pointer text-primary hover:underline"
          onClick={(event) => {
            event.stopPropagation();
            if (name === 'trace_id') {
              onTraceOpen(value);
            } else {
              onSpanOpen();
            }
          }}
          onKeyDown={(event) => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              event.stopPropagation();
              if (name === 'trace_id') onTraceOpen(value);
              else onSpanOpen();
            }
          }}
        >
          {value.length > 12 ? `${value.slice(0, 12)}…` : value}
        </span>
      ) : (
        <span className="text-faint">-</span>
      );
    default: {
      const text = typeof value === 'object' ? JSON.stringify(value) : String(value);
      return (
        <span className="block truncate" title={text}>
          {text}
        </span>
      );
    }
  }
}
