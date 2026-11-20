import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { LocateFixed, X } from 'lucide-react';
import { Button, Empty, Select, SpinArea } from '@/design-system';
import type { SearchDomain } from '@/api/types';
import { formatDateTimeMs, formatDurationUs } from '@/utils/format';
import { searchSql } from '@/api/search';
import { cn } from '@/utils/cn';
import { ErrorHint, type DetailRow } from './detailShared';
import {
  CONTEXT_DEFAULT_WINDOW_MS,
  CONTEXT_LIMIT,
  CONTEXT_MAX_MATCHES,
  contextIdentity,
  CONTEXT_WINDOWS,
  buildAvailableFields,
  buildContextQuery,
  type ContextMatchEntry,
  type ContextMatchMode,
} from './surroundingContext';

/** chip 按钮（时间窗档位 / 匹配方式 / 匹配属性共用，选中态 primary-soft）. */
function Chip({ selected, disabled, onClick, children, title, ariaLabel }: { selected?: boolean; disabled?: boolean; onClick: () => void; children: React.ReactNode; title?: string; ariaLabel?: string }) {
  return (
    <button
      type="button"
      title={title}
      aria-label={ariaLabel}
      disabled={disabled}
      className={cn(
        'max-w-full cursor-pointer truncate rounded-sm border px-1.5 py-0.5 font-mono text-[11px] transition-colors',
        selected ? 'border-primary bg-primary-soft text-primary' : 'border-border bg-surface-2 text-muted hover:text-fg',
      )}
      onClick={onClick}
    >
      {children}
    </button>
  );
}

/**
 * 上下文 tab（HyperDX Surrounding Context 式）：±时间窗 8 档 + Match on（任意/同服务/属性点选）。
 * 属性匹配条件经 buildContextQuery 白名单编译 + 参数绑定；结果点击切换抽屉目标，当前记录高亮。
 */
export function SurroundingContextTab({ record: initialRecord, currentTs, onSelect, scrollRoot, domain: initialDomain = 'logs', allowDomainSwitch = false }: { record: DetailRow; currentTs: string; onSelect: (row: DetailRow, domain?: SearchDomain) => void; scrollRoot?: RefObject<HTMLDivElement | null>; domain?: SearchDomain; allowDomainSwitch?: boolean }) {
  const [record, setRecord] = useState(initialRecord);
  const [domain, setDomain] = useState<SearchDomain>(initialDomain);
  const [preview, setPreview] = useState<DetailRow | null>(null);
  const { t } = useTranslation();
  const [windowMs, setWindowMs] = useState<number>(CONTEXT_DEFAULT_WINDOW_MS);
  const [mode, setMode] = useState<ContextMatchMode>('anything');
  const [matching, setMatching] = useState<ContextMatchEntry[]>([]);
  const [rows, setRows] = useState<DetailRow[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const sentinelRef = useRef<HTMLDivElement>(null);
  const generationRef = useRef(0);
  const pendingRef = useRef(false);
  const offsetRef = useRef(0);

  const previousRecord = useRef(initialRecord);
  useEffect(() => {
    if (previousRecord.current === initialRecord) return;
    previousRecord.current = initialRecord;
    setRecord(initialRecord); setPreview(null); setMatching([]); setMode('anything');
  }, [initialRecord]);
  const available = useMemo(() => buildAvailableFields(record, domain), [record, domain]);
  const hostAvailable = available.some((entry) => entry.field === 'resource["host.name"]');

  const loadPage = useCallback(async () => {
    if (pendingRef.current || !record.ts) return;
    const generation = generationRef.current;
    pendingRef.current = true;
    setLoading(true);
    setError(null);
    try {
      const request = buildContextQuery({ record, windowMs, mode, matching, domain, offset: offsetRef.current });
      const response = await searchSql({ ...request, rowLimit: CONTEXT_LIMIT });
      if (generation !== generationRef.current) return;
      const nextRows = response.rows.map((cells) => Object.fromEntries(response.columns.map((name, i) => [name, cells[i]])));
      offsetRef.current += nextRows.length;
      setRows((previous) => [...(previous ?? []), ...nextRows]);
      setHasMore(response.truncated && nextRows.length > 0);
    } catch (err: unknown) {
      if (generation !== generationRef.current) return;
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      if (generation === generationRef.current) {
        pendingRef.current = false;
        setLoading(false);
      }
    }
  }, [record, currentTs, windowMs, mode, matching, domain]);

  // 条件变化时重置分页；旧响应不能混入新列表。
  useEffect(() => {
    generationRef.current += 1;
    pendingRef.current = false;
    offsetRef.current = 0;
    setRows(null);
    setHasMore(false);
    setError(null);
    void loadPage();
    return () => { generationRef.current += 1; };
  }, [loadPage]);

  useEffect(() => {
    const sentinel = sentinelRef.current;
    if (!sentinel || !hasMore || loading || error !== null || typeof IntersectionObserver === 'undefined') return;
    let active = true;
    const observer = new IntersectionObserver((entries) => {
      if (active && entries.some((entry) => entry.isIntersecting)) void loadPage();
    }, { root: scrollRoot?.current ?? null, rootMargin: '0px 0px 160px 0px' });
    observer.observe(sentinel);
    return () => { active = false; observer.disconnect(); };
  }, [hasMore, loading, error, loadPage, scrollRoot]);

  const addMatch = (entry: ContextMatchEntry) => {
    if (matching.length >= CONTEXT_MAX_MATCHES) return;
    setMode('attrs');
    setMatching((prev) => (prev.some((item) => item.field === entry.field) ? prev : [...prev, entry]));
  };
  const removeMatch = (field: string) => {
    setMatching((prev) => prev.filter((item) => item.field !== field));
  };

  const matchedFields = new Set(matching.map((item) => item.field));

  return (
    <div className="flex flex-col gap-3">
      {allowDomainSwitch && <Select value={domain} aria-label={t('search.contextResultType')} options={[{ value: 'traces', label: t('search.sourceTraces') }, { value: 'logs', label: t('search.sourceLogs') }]} onValueChange={(value) => { setDomain(value as SearchDomain); setMatching([]); setMode('anything'); setPreview(null); }} />}
      {preview && <Button size="sm" variant="link" icon={<LocateFixed />} onClick={() => { setRecord(preview); setMatching([]); setMode('anything'); setPreview(null); }}>{t('search.contextReanchor')}</Button>}
      {/* ±时间窗档位 */}
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-subtle">±{windowMs >= 60_000 ? `${windowMs / 60_000}m` : `${windowMs / 1000}s`}</span>
        {CONTEXT_WINDOWS.map((option) => (
          <Chip key={option.label} selected={windowMs === option.ms} onClick={() => setWindowMs(option.ms)}>
            {option.label}
          </Chip>
        ))}
      </div>
      {/* Match on */}
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-subtle">{t('search.contextMatchOn')}</span>
        <Chip selected={mode === 'anything'} onClick={() => setMode('anything')}>{t('search.contextMatchAnything')}</Chip>
        <Chip selected={mode === 'service'} onClick={() => setMode('service')}>{t('search.contextMatchService')}</Chip>
        {hostAvailable && <Chip selected={mode === 'host'} onClick={() => setMode('host')}>{t('search.contextMatchHost')}</Chip>}
        {!hostAvailable && <span className="text-xs text-faint">{t('search.contextHostMissing')}</span>}
        <Chip selected={mode === 'attrs'} onClick={() => setMode('attrs')}>{t('search.contextMatchAttrs')}</Chip>
      </div>
      {/* 匹配属性管理（两段式：已选匹配 / 可点选添加，HyperDX 同款） */}
      <div className="flex flex-col gap-1.5">
        <span className="text-xs text-subtle">{t('search.contextMatchingAttrs', { count: matching.length })}</span>
        {matching.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {matching.map((entry) => (
              <Chip
                key={entry.field}
                selected
                title={t('search.contextRemoveMatch')}
                ariaLabel={`${t('search.contextRemoveMatch')}: ${entry.key}`}
                onClick={() => removeMatch(entry.field)}
              >
                {entry.key} = {entry.value} <X className="inline size-3 align-[-2px]" aria-hidden />
              </Chip>
            ))}
          </div>
        )}
        <span className="text-[11px] text-faint">{t('search.contextAvailableToAdd')}</span>
        <div className="flex flex-wrap gap-1.5">
          {available.map((entry) => (
            <Chip key={entry.field} disabled={!matchedFields.has(entry.field) && matching.length >= CONTEXT_MAX_MATCHES} selected={matchedFields.has(entry.field)} title={t('search.contextAddMatch')} onClick={() => addMatch(entry)}>
              {entry.key} = {entry.value}
            </Chip>
          ))}
        </div>
      </div>
      {/* 结果列表 */}
      {rows !== null && rows.length === 0 && !loading && error === null && <Empty description={t('search.contextEmpty')} />}
      {rows !== null && rows.length > 0 && <div className="overflow-x-auto">
        <table className="w-full text-left font-mono text-xs">
          <thead className="border-b border-border text-subtle"><tr>
            <th className="px-2 py-2">{t('search.miniColTime')}</th>
            <th className="px-2">{t('search.miniColService')}</th>
            <th className="px-2">{domain === 'traces' ? t('search.traceStatus') : t('search.miniColSeverity')}</th>
            {domain === 'traces' && <th className="px-2">{t('search.traceDuration')}</th>}
            <th className="px-2">{domain === 'traces' ? t('search.traceSpanName') : t('search.miniColBody')}</th>
          </tr></thead>
          <tbody>{rows.map((item, index) => <tr key={contextIdentity(item, domain) + index}
            className={cn('cursor-pointer border-b border-border/40 hover:bg-surface-2', contextIdentity(item, domain) === contextIdentity(record, domain) && 'bg-primary-soft')}
            onClick={() => { setPreview(item); onSelect(item, domain); }}>
            <td className="whitespace-nowrap px-2 py-1.5"><button type="button" onClick={(event) => { event.stopPropagation(); setPreview(item); onSelect(item, domain); }}>{formatDateTimeMs(String(item.ts ?? ''))}</button></td>
            <td className="max-w-40 truncate px-2">{String(item.service ?? '-')}</td>
            <td className={cn('px-2', String(item.status ?? item.severity).toLowerCase() === 'error' && 'text-error')}>{String(item.status ?? item.severity ?? '-')}</td>
            {domain === 'traces' && <td className="whitespace-nowrap px-2" title={String(item.duration_us) + ' µs'}>{formatDurationUs(Number(item.duration_us))}</td>}
            <td className="max-w-96 truncate px-2" title={String(item.name ?? item.body ?? '')}>{String(item.name ?? item.body ?? '-')}</td>
          </tr>)}</tbody>
        </table>
      </div>}
      <div ref={sentinelRef} data-testid="context-load-more" className="min-h-1">
        {loading && <SpinArea />}
        {error !== null && <ErrorHint message={error} onRetry={() => void loadPage()} />}
        {rows !== null && !loading && error === null && !hasMore && (
          <div role="status" className="rounded-sm bg-surface-2 px-3 py-4 text-center font-mono text-xs text-muted">{t('search.endOfResults')}</div>
        )}
      </div>
    </div>
  );
}
