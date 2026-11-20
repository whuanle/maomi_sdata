import { useState } from 'react';
import { ChevronDown, PanelLeftClose, PanelLeftOpen, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { SearchDomain } from '@/api/types';
import { Checkbox, Input, Tooltip } from '@/design-system';
import { cn } from '@/utils/cn';
import { FilterTree } from './FilterTree';
import { formatFilterChip, type SearchFilter } from './filters';

/** 分析模式（patterns 为 HyperDX Event Patterns 占位）. */
export type AnalysisMode = 'table' | 'patterns';

interface SearchSidePanelProps {
  domain: SearchDomain;
  mode: AnalysisMode;
  onModeChange: (mode: AnalysisMode) => void;
  denoise: boolean;
  onDenoiseChange: (value: boolean) => void;
  filters: SearchFilter[];
  onFiltersChange: (filters: SearchFilter[]) => void;
  /** 最近一次显式查询的时间窗（ISO；轮询保持不变，未执行为 null） */
  from: string | null;
  to: string | null;
}

/** 搜索页左侧面板（HyperDX Analysis Mode）：模式切换 + 过滤器树（属性分组/值勾选，「更多过滤器」底栏切换显隐）+ chips. */
export function SearchSidePanel({ domain, mode, onModeChange, denoise, onDenoiseChange, filters, onFiltersChange, from, to }: SearchSidePanelProps) {
  const { t } = useTranslation();
  const [collapsed, setCollapsed] = useState(false);
  const [filtersOpen, setFiltersOpen] = useState(true);
  const [treeOpen, setTreeOpen] = useState(false);
  const showTree = domain === 'traces' || treeOpen;
  const setDuration = (op: 'gte' | 'lte', value: string) => {
    const rest = filters.filter((filter) => !(filter.field === 'duration_us' && filter.op === op));
    const ms = Number(value);
    onFiltersChange(value.trim() && Number.isFinite(ms) && ms >= 0
      ? [...rest, { id: Date.now(), field: 'duration_us', op, value: String(ms * 1000) }]
      : rest);
  };

  if (collapsed) {
    return (
      <div
        className="flex w-9 shrink-0 flex-col items-center py-2"
        data-testid="search-side-panel"
      >
        <button
          type="button"
          aria-label={t('search.panelExpand')}
          title={t('search.panelExpand')}
          onClick={() => setCollapsed(false)}
          className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
        >
          <PanelLeftOpen className="size-3.5" aria-hidden />
        </button>
      </div>
    );
  }

  return (
    <div
      className="flex w-72 shrink-0 flex-col overflow-auto px-3 py-2.5"
      data-testid="search-side-panel"
    >
      <div className="flex items-center justify-between">
        <span className="text-xs text-subtle">{t('search.analysisMode')}</span>
        <button
          type="button"
          aria-label={t('search.panelCollapse')}
          title={t('search.panelCollapse')}
          onClick={() => setCollapsed(true)}
          className="cursor-pointer rounded-sm p-0.5 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
        >
          <PanelLeftClose className="size-3.5" aria-hidden />
        </button>
      </div>

      <div className="mt-1.5 flex flex-col gap-0.5" data-testid="search-analysis-mode">
        {(
          [
            { value: 'table', label: t('search.modeResultsTable') },
            { value: 'patterns', label: t('search.modeEventPatterns') },
          ] as const
        ).map((item) => (
          <button
            key={item.value}
            type="button"
            onClick={() => onModeChange(item.value)}
            className={cn(
              'cursor-pointer rounded-sm border-l-2 px-2 py-1 text-left text-[13px] transition-colors',
              mode === item.value
                ? 'border-accent bg-surface-2 font-medium text-fg'
                : 'border-transparent text-muted hover:bg-surface-2 hover:text-fg',
            )}
          >
            {item.label}
          </button>
        ))}
      </div>

      <div className="mt-2.5 border-t border-border pt-2.5">
        <button
          type="button"
          onClick={() => setFiltersOpen(!filtersOpen)}
          className="flex w-full cursor-pointer items-center justify-between rounded-sm py-0.5 text-xs text-subtle transition-colors hover:text-fg"
        >
          {t('search.filters')}
          <ChevronDown className={cn('size-3 transition-transform', filtersOpen && 'rotate-180')} aria-hidden />
        </button>

        {filtersOpen && (
          <>
            {domain === 'logs' && <div className="mt-1.5">
              <Tooltip content={domain === 'logs' ? t('search.denoiseTooltip') : t('search.denoiseDisabled')} side="right">
                <span>
                  <Checkbox
                    checked={denoise}
                    disabled={domain !== 'logs'}
                    onChange={(event) => onDenoiseChange(event.target.checked)}
                    label={t('search.denoise')}
                    data-testid="search-denoise"
                  />
                </span>
              </Tooltip>
            </div>}
            {domain === 'traces' && (
              <div className="my-2 flex flex-col gap-2">
                <Checkbox
                  checked={filters.some((filter) => filter.op === 'root')}
                  label={t('search.rootSpansOnly')}
                  onChange={(event) => onFiltersChange([
                    ...filters.filter((filter) => filter.op !== 'root'),
                    ...(event.target.checked ? [{ id: Date.now(), field: 'parent_span_id', op: 'root' as const, value: '' }] : []),
                  ])}
                />
                <span className="text-xs text-muted">{t('search.durationRangeMs')}</span>
                <div className="grid grid-cols-2 gap-2">
                  {(['gte', 'lte'] as const).map((op) => (
                    <Input key={op} type="number" min="0" step="any"
                      aria-label={t(op === 'gte' ? 'search.durationMin' : 'search.durationMax')}
                      placeholder={t(op === 'gte' ? 'search.durationMin' : 'search.durationMax')}
                      value={filters.find((filter) => filter.field === 'duration_us' && filter.op === op)?.value
                        ? String(Number(filters.find((filter) => filter.field === 'duration_us' && filter.op === op)!.value) / 1000) : ''}
                      onChange={(event) => setDuration(op, event.target.value)}
                    />
                  ))}
                </div>
              </div>
            )}

            {/* 过滤器树：默认收起，「更多过滤器/收起过滤器」底栏切换显隐（数据域切换时重挂载，分组配置与 hook 数量随域变化） */}
            {showTree && from && to && (
              <FilterTree
                key={domain}
                domain={domain}
                from={from}
                to={to}
                denoise={denoise}
                filters={filters}
                onFiltersChange={onFiltersChange}
              />
            )}

            {filters.length === 0 && (
              <div className="mt-2 text-xs text-subtle">{t('search.noFilters')}</div>
            )}

            <div className="mt-1.5 flex flex-col gap-1">
              {filters.map((filter) => (
                <span
                  key={filter.id}
                  data-testid="search-filter-chip"
                  className="flex max-w-full items-center gap-1 rounded-sm border border-primary/30 bg-primary-soft px-1.5 py-1 font-mono text-[11px] text-fg"
                >
                  <span className="min-w-0 truncate">{formatFilterChip(filter)}</span>
                  <button
                    type="button"
                    aria-label="remove filter"
                    onClick={() => onFiltersChange(filters.filter((item) => item.id !== filter.id))}
                    className="shrink-0 cursor-pointer text-subtle transition-colors hover:text-error"
                  >
                    <X className="size-3" aria-hidden />
                  </button>
                </span>
              ))}
            </div>

            {/* HyperDX 式底栏：展开属性过滤器树（避免常驻占据侧栏），手动过滤走搜索栏 Lucene 表达式 */}
            {domain === 'logs' && <button
              type="button"
              data-testid="search-toggle-filters"
              onClick={() => setTreeOpen(!treeOpen)}
              className={cn(
                'mt-2 flex w-full cursor-pointer items-center justify-center gap-1 rounded-sm border bg-surface px-2 py-1.5 text-xs transition-colors',
                treeOpen ? 'border-primary/60 text-fg' : 'border-border text-muted hover:border-border-strong hover:text-fg',
              )}
            >
              {treeOpen ? t('search.lessFilters') : t('search.moreFilters')}
              <ChevronDown className={cn('size-3 transition-transform', treeOpen && 'rotate-180')} aria-hidden />
            </button>}

            {treeOpen && (
              <div className="mt-2 text-[11px] leading-relaxed" data-testid="search-facet-hint">
                <div className="font-medium text-muted">{t('search.facetTrySearchTitle')}</div>
                <div className="text-subtle">{t('search.facetTrySearch')}</div>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
