import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import dayjs from 'dayjs';
import { Copy, Crosshair, ExternalLink, Filter, Network, RefreshCw } from 'lucide-react';
import { Button, Drawer, Empty, SpinArea, Tag, Tabs, TabsContent, TabsList, TabsTrigger, toast, Tooltip } from '@/design-system';
import { searchSql } from '@/api/search';
import type { SearchDomain } from '@/api/types';import { cn } from '@/utils/cn';
import { formatDateTimeMs, formatRelativeTime } from '@/utils/format';
import { prettyJson, renderDetailValue, SEVERITY_TEXT_CLS } from './detailFormat';
import { JsonTree } from './JsonTree';
import { ErrorHint, MiniLogList, SearchBox, Section, type DetailRow } from './detailShared';
import { ServiceMapTab } from './ServiceMapTab';
import { SurroundingContextTab } from './SurroundingContextTab';
import { LogBodyPreview } from './LogBodyPreview';
import { spanEvents } from './traceTimeline';

/** 详情抽屉宽度（ClickStack/HyperDX 式侧栏：至少窗体一半，小窗下兜底 640px）. */
const DRAWER_WIDTH = 'max(50vw, 640px)';

/** 链路日志回看窗（目标行 ±1h，覆盖常规 trace 生命周期）. */
const TRACE_LOGS_WINDOW_MIN = 60;

/** 链路日志列表行数上限. */
const RELATED_LIMIT = 200;

/** span status → Tag 色调. */
const STATUS_TONE: Record<string, 'error' | 'success' | 'default'> = {
  error: 'error',
  ok: 'success',
  unset: 'default',
};

/** JSON 对象字符串 → 顶层键值对（非对象/解析失败返回空数组）. */
function parseJsonObject(value: unknown): [string, unknown][] {
  if (typeof value !== 'string' || value.length === 0) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(value);
    if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return Object.entries(parsed);
    }

    return [];
  } catch {
    return [];
  }
}

/** 兼容历史 OTLP 纳秒时间与规范化事件. */
const parseJsonEvents = spanEvents;

/** 按关键字（键或值）过滤键值对. */
function filterEntries(entries: [string, unknown][], keyword: string): [string, unknown][] {
  const text = keyword.trim().toLowerCase();
  if (text.length === 0) {
    return entries;
  }

  return entries.filter(([key, value]) => key.toLowerCase().includes(text) || String(value).toLowerCase().includes(text));
}

export interface LogDetailDrawerProps {
  /** 结果表数据域（决定身份补查的表与默认布局） */
  domain: SearchDomain;
  /** 目标行（结果表所选列的值；null = 抽屉关闭） */
  row: DetailRow | null;
  onClose: () => void;
  /** 「按此值过滤」（field 允许 attributes./resource. 路径） */
  onFilterByValue: (field: string, value: unknown) => void;
  /** 「仅此值」（清空该字段其它值过滤，仅保留此值） */
  onFilterOnlyValue: (field: string, value: unknown) => void;
  /** trace_id 激活（跳转链路视图 / 打开瀑布图，由宿主按域分流） */
  onTraceActivate: (traceId: string) => void;
}

/**
 * ClickStack/HyperDX 式日志详情抽屉：行点击后按「时间戳 + 服务（+ trace_id/span_id/severity）」身份条件
 * 经 analytics/search 补查完整记录（含 attributes/resource 等未选列），右侧滑出展示：
 * 头部元信息（severity/绝对+相对时间/服务/Trace ID + 查看链路）→ Body 全文 →
 * 五个 tab：概览（顶层/日志/资源属性）、列值（全字段 + 按值过滤/仅此值）、链路日志（同 trace ±1h）、
 * 服务地图（trace 内服务拓扑，仅 trace_id）、上下文（±窗 + Match on 属性匹配，仅日志）。
 */
export function LogDetailDrawer({ domain, row, onClose, onFilterByValue, onFilterOnlyValue, onTraceActivate }: LogDetailDrawerProps) {
  const { t } = useTranslation();
  const [target, setTarget] = useState<DetailRow | null>(null);
  // 目标域（抽屉内切换到链路日志条目后变为 logs）
  const [targetDomain, setTargetDomain] = useState<SearchDomain>(domain);
  const [record, setRecord] = useState<DetailRow | null>(null);
  const [recordLoading, setRecordLoading] = useState(false);
  const [recordError, setRecordError] = useState<string | null>(null);
  const [retryTick, setRetryTick] = useState(0);
  const [tab, setTab] = useState('overview');
  const [contextSelection, setContextSelection] = useState<DetailRow | null>(null);
  const scrollRootRef = useRef<HTMLDivElement>(null);
  // 属性搜索框（HyperDX 式 per-section 过滤）
  const [topSearch, setTopSearch] = useState('');
  const [attrSearch, setAttrSearch] = useState('');

  // 链路日志列表（懒加载：首次进入 tab 才查询；按目标行失效）
  const [traceLogs, setTraceLogs] = useState<DetailRow[] | null>(null);
  const [traceLogsLoading, setTraceLogsLoading] = useState(false);
  const [traceLogsError, setTraceLogsError] = useState<string | null>(null);
  const relatedTickRef = useRef(0);

  // 目标行变化：整体重置（含懒加载状态与页签回概览）
  useEffect(() => {
    setContextSelection(null);
    setTarget(row);
    setTargetDomain(domain);
    setRecord(null);
    setRecordError(null);
    setRetryTick((tick) => tick + 1);
    setTab('overview');
    setTopSearch('');
    setAttrSearch('');
    setTraceLogs(null);
    setTraceLogsError(null);
    relatedTickRef.current += 1;
  }, [row, domain]);

  const tsValue = target !== null && typeof target.ts !== 'undefined' && target.ts !== null ? String(target.ts) : '';

  // 完整记录补查（失败回退为结果表行数据，仍可浏览所选列）
  useEffect(() => {
    if (target === null || tsValue.length === 0) {
      return;
    }

    const tick = relatedTickRef.current;
    setRecordLoading(true);
    setRecordError(null);
    fetchRecordByRow(target, targetDomain, tsValue)
      .then((fetched) => {
        if (relatedTickRef.current !== tick) return;
        setRecord(fetched ?? target);
      })
      .catch((err: unknown) => {
        if (relatedTickRef.current !== tick) return;
        setRecordError(err instanceof Error ? err.message : String(err));
        setRecord(target);
      })
      .finally(() => {
        if (relatedTickRef.current !== tick) return;
        setRecordLoading(false);
      });
  }, [target, targetDomain, tsValue, retryTick]);

  /** 记录种类：日志（含 body）或 span（traces 域列）. */
  const kind: 'log' | 'span' = useMemo(() => {
    const source = record ?? target;
    return source !== null && 'body' in source ? 'log' : 'span';
  }, [record, target]);

  const traceId = useMemo(() => {
    const value = (record ?? target)?.trace_id;
    return typeof value === 'string' && value.length > 0 ? value : '';
  }, [record, target]);

  // 链路日志列表：进入 tab 后按 trace_id 查询（±1h 窗）
  useEffect(() => {
    if (tab !== 'traceLogs' || traceId.length === 0 || tsValue.length === 0 || traceLogs !== null || traceLogsError !== null) {
      return;
    }

    const tick = relatedTickRef.current;
    const from = dayjs(tsValue).subtract(TRACE_LOGS_WINDOW_MIN, 'minute');
    const to = dayjs(tsValue).add(TRACE_LOGS_WINDOW_MIN, 'minute');
    setTraceLogsLoading(true);
    searchSql({
      sql: [
        'SELECT ts, service, severity, body, trace_id, span_id',
        'FROM {{logs}}',
        'WHERE trace_id = $trace_id AND ts >= $start AND ts < $end',
        'ORDER BY ts ASC',
        `LIMIT ${RELATED_LIMIT}`,
      ].join('\n'),
      from: from.toISOString(),
      to: to.toISOString(),
      rowLimit: RELATED_LIMIT,
      parameters: { trace_id: traceId },
    })
      .then((response) => {
        if (relatedTickRef.current !== tick) return;
        setTraceLogs(response.rows.map((cells) => Object.fromEntries(response.columns.map((name, i) => [name, cells[i]]))));
      })
      .catch((err: unknown) => {
        if (relatedTickRef.current !== tick) return;
        setTraceLogsError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (relatedTickRef.current !== tick) return;
        setTraceLogsLoading(false);
      });
  }, [tab, traceId, tsValue, traceLogs, traceLogsError, retryTick]);

  /** 抽屉内切换目标（链路日志/上下文条目点击）. */
  const switchToRow = (nextRow: DetailRow) => {
    relatedTickRef.current += 1;
    setTargetDomain('logs');
    setTarget(nextRow);
    setRecord(null);
    setRecordError(null);
    setTab('overview');
    setTopSearch('');
    setAttrSearch('');
    setTraceLogs(null);
    setTraceLogsError(null);
  };

  const copyText = (text: string) => {
    try {
      void navigator.clipboard?.writeText(text);
      toast.success(t('search.copied'));
    } catch {
      // 剪贴板不可用时静默（如非安全上下文）
    }
  };

  if (target === null) {
    return <Drawer open={false} onOpenChange={() => undefined} width={DRAWER_WIDTH}>{null}</Drawer>;
  }

  const current = record ?? target;
  const severityText = kind === 'log' ? String(current.severity ?? '') : '';
  const serviceText = String(current.service ?? '-');
  const bodyText = typeof current.body === 'string' && current.body.length > 0 ? current.body : '-';
  const viewTraceLabel = domain === 'logs' ? t('search.viewTrace') : t('search.viewWaterfall');

  return (
    <Drawer
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      width={DRAWER_WIDTH}
      bodyRef={scrollRootRef}
      title={(
        <div className="flex min-w-0 flex-col gap-1" data-testid="log-detail-drawer">
          {/* 元信息行：severity · 时间（绝对+相对）· Service · Trace ID + 查看链路 */}
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
            {severityText.length > 0 && (
              <span className={cn('font-mono text-xs', SEVERITY_TEXT_CLS[severityText] ?? 'text-fg')}>{severityText}</span>
            )}
            {kind === 'span' && (
              <span className="font-mono text-xs text-info">{String(current.status ?? '')}</span>
            )}
            <span className="whitespace-nowrap font-mono text-[13px]">{tsValue.length > 0 ? formatDateTimeMs(tsValue) : '-'}</span>
            <span className="whitespace-nowrap text-xs text-muted">{formatRelativeTime(tsValue)}</span>
            <span className="whitespace-nowrap text-xs text-subtle">{t('search.headerService')}</span>
            <span className="truncate font-mono text-[13px]">{serviceText}</span>
            <span className="flex min-w-0 items-center gap-1.5">
              {traceId.length > 0 && (
                <>
                  <span className="whitespace-nowrap text-xs text-subtle">{t('search.headerTraceId')}</span>
                  <button
                    type="button"
                    className="flex w-fit cursor-pointer items-center gap-1 font-mono text-[11px] text-primary hover:underline"
                    onClick={() => onTraceActivate(traceId)}
                    title={traceId}
                  >
                    <Network className="size-3 shrink-0" aria-hidden />
                    {traceId}
                  </button>
                </>
              )}
              {traceId.length > 0 && (
                <Tooltip content={t('search.viewTraceTooltip')}>
                  <Button size="sm" variant="secondary" icon={<Network className="size-3.5" aria-hidden />} onClick={() => onTraceActivate(traceId)}>
                    {viewTraceLabel}
                  </Button>
                </Tooltip>
              )}
            </span>
          </div>
        </div>
      )}
    >
      <div className="flex min-w-0 max-w-full flex-col gap-4">
        {recordError !== null && (
          <div className="flex items-center justify-between gap-2 rounded-md border border-warning/40 bg-warning/10 px-2.5 py-1.5 text-xs text-warning">
            <span className="min-w-0 break-all">{t('search.detailLoadFailed')}</span>
            <button
              type="button"
              className="flex shrink-0 cursor-pointer items-center gap-1 hover:underline"
              onClick={() => setRetryTick((tick) => tick + 1)}
            >
              <RefreshCw className="size-3" aria-hidden />
              {t('search.retry')}
            </button>
          </div>
        )}
        {recordLoading && record === null ? (
          <SpinArea />
        ) : (
          <>
            {/* 消息体默认收缩，完整内容可展开并在区域内滚动。 */}
            {kind === 'log' && (
              <div className="flex min-w-0 max-w-full flex-col gap-1.5">
                <LogBodyPreview key={`${tsValue}:${String(current.id ?? '')}`} text={bodyText} onCopy={copyText} />
                <div>
                  <button
                    type="button"
                    className="max-w-full cursor-pointer truncate rounded-sm border border-border bg-surface-2 px-1.5 py-0.5 font-mono text-[11px] text-muted transition-colors hover:text-fg"
                    onClick={() => copyText(`ServiceName: ${serviceText}`)}
                  >
                    <span className="text-subtle">ServiceName: </span>
                    {serviceText}
                  </button>
                </div>
              </div>
            )}
            <Tabs value={tab} onValueChange={setTab}>
              <TabsList>
                <TabsTrigger value="overview">{t('search.drawerTabOverview')}</TabsTrigger>
                <TabsTrigger value="values">{t('search.drawerTabValues')}</TabsTrigger>
                {traceId.length > 0 && <TabsTrigger value="traceLogs">{t('search.drawerTabTraceLogs')}</TabsTrigger>}
                {traceId.length > 0 && <TabsTrigger value="serviceMap">{t('search.drawerTabServiceMap')}</TabsTrigger>}
                {kind === 'log' && <TabsTrigger value="context">{t('search.drawerTabContext')}</TabsTrigger>}
              </TabsList>
              <TabsContent value="overview" className="flex flex-col gap-5 pt-3">
                <OverviewTab
                  kind={kind}
                  record={current}
                  topSearch={topSearch}
                  attrSearch={attrSearch}
                  onTopSearch={setTopSearch}
                  onAttrSearch={setAttrSearch}
                  onCopy={copyText}
                  onTraceActivate={onTraceActivate}
                  onFilterByValue={onFilterByValue}
                  onFilterOnlyValue={onFilterOnlyValue}
                />
              </TabsContent>
              <TabsContent value="values" className="flex flex-col gap-2 pt-3">
                <ValuesTab record={current} onCopy={copyText} />
              </TabsContent>
              {traceId.length > 0 && (
                <TabsContent value="traceLogs" className="flex flex-col gap-2 pt-3">
                  {/* Open in search（HyperDX 式：以该 TraceId 过滤回到主搜索，收起抽屉看结果） */}
                  <div>
                    <Button
                      size="sm"
                      variant="secondary"
                      icon={<ExternalLink className="size-3.5" aria-hidden />}
                      onClick={() => {
                        onFilterByValue('trace_id', traceId);
                        onClose();
                      }}
                    >
                      {t('search.drawerOpenInSearch')}
                    </Button>
                  </div>
                  {traceLogsLoading && <SpinArea />}
                  {traceLogsError !== null && (
                    <ErrorHint message={traceLogsError} onRetry={() => { setTraceLogsError(null); setRetryTick((tick) => tick + 1); }} />
                  )}
                  {traceLogs !== null && traceLogs.length === 0 && !traceLogsLoading && (
                    <Empty description={t('search.traceLogsEmpty')} />
                  )}
                  {traceLogs !== null && traceLogs.length > 0 && (
                    <MiniLogList rows={traceLogs} currentTs={tsValue} onSelect={switchToRow} showHeader />
                  )}
                </TabsContent>
              )}
              {traceId.length > 0 && (
                <TabsContent value="serviceMap" className="pt-3">
                  <ServiceMapTab key={traceId} traceId={traceId} />
                </TabsContent>
              )}
              {kind === 'log' && (
                <TabsContent value="context" className="pt-3">
                  <SurroundingContextTab key={tsValue} record={current} currentTs={tsValue} onSelect={setContextSelection} scrollRoot={scrollRootRef} />
                </TabsContent>
              )}
            </Tabs>
          </>
        )}
      </div>
      {contextSelection && <LogDetailDrawer domain="logs" row={contextSelection} onClose={() => setContextSelection(null)} onFilterByValue={onFilterByValue} onFilterOnlyValue={onFilterOnlyValue} onTraceActivate={onTraceActivate} />}
    </Drawer>
  );
}

/** 键值对列表（明细值渲染 + 悬浮动作：按此值过滤/仅此值/复制；trace_id 可点击激活链路）. */
function KeyValueList({
  entries,
  search,
  onCopy,
  onFilterByValue,
  onFilterOnlyValue,
  onTraceActivate,
  fieldPrefix = '',
}: {
  entries: [string, unknown][];
  search: string;
  onCopy: (text: string) => void;
  onFilterByValue?: (field: string, value: unknown) => void;
  onFilterOnlyValue?: (field: string, value: unknown) => void;
  onTraceActivate?: (traceId: string) => void;
  /** 属性键过滤路径前缀（'attributes.' / 'resource.'，顶层列为空） */
  fieldPrefix?: string;
}) {
  const { t } = useTranslation();
  const filtered = filterEntries(entries, search);
  if (filtered.length === 0) {
    return <div className="text-xs text-faint">{t('search.attrNoMatch')}</div>;
  }

  return (
    <div className="grid grid-cols-[150px_minmax(0,1fr)] gap-x-3 gap-y-1.5">
      {filtered.map(([key, value]) => {
        const text = renderDetailValue(key, value);
        const isTraceId = key === 'trace_id' && typeof value === 'string' && value.length > 0;
        return (
          <FragmentedKeyValue
            key={key}
            k={key}
            field={fieldPrefix + key}
            text={text}
            raw={typeof value === 'object' ? prettyJson(value) : String(value)}
            onCopy={onCopy}
            onFilterByValue={onFilterByValue}
            onFilterOnlyValue={onFilterOnlyValue}
            isTraceId={isTraceId}
            onTraceActivate={onTraceActivate}
            copyLabel={t('search.copyValue')}
          />
        );
      })}
    </div>
  );
}

/** 单键值行（键 + 着色值 + 悬浮动作：按此值过滤/仅此值/复制；ts 不可作过滤字段）. */
function FragmentedKeyValue({
  k,
  field,
  text,
  raw,
  onCopy,
  onFilterByValue,
  onFilterOnlyValue,
  isTraceId,
  onTraceActivate,
  copyLabel,
}: {
  k: string;
  field: string;
  text: string;
  raw: string;
  onCopy: (text: string) => void;
  onFilterByValue?: (field: string, value: unknown) => void;
  onFilterOnlyValue?: (field: string, value: unknown) => void;
  isTraceId: boolean;
  onTraceActivate?: (traceId: string) => void;
  copyLabel: string;
}) {
  const { t } = useTranslation();
  const isColored = k === 'severity' || k === 'status';
  const filterable = field !== 'ts' && raw.length > 0 && onFilterByValue !== undefined;
  return (
    <>
      <div className="pt-0.5 font-sans text-xs text-subtle" title={k}>{k}</div>
      <div className="group/kv flex min-w-0 items-start gap-1">
        {isTraceId && onTraceActivate ? (
          <button
            type="button"
            className="min-w-0 flex-1 cursor-pointer text-left font-mono text-xs break-all text-primary hover:underline"
            title={raw}
            onClick={() => onTraceActivate(raw)}
          >
            {text}
          </button>
        ) : (
          <pre
            className={cn(
              'min-w-0 flex-1 font-mono text-xs break-all whitespace-pre-wrap',
              isColored ? SEVERITY_TEXT_CLS[String(raw)] ?? 'text-fg' : 'text-fg',
            )}
          >
            {text}
          </pre>
        )}
        {filterable && (
          <Tooltip content={t('search.filterByValue')}>
            <button
              type="button"
              aria-label={`${t('search.filterByValue')}: ${field}`}
              className="shrink-0 cursor-pointer p-0.5 text-faint opacity-0 transition-opacity group-hover/kv:opacity-100 hover:text-primary"
              onClick={() => onFilterByValue?.(field, raw)}
            >
              <Filter className="size-3.5" aria-hidden />
            </button>
          </Tooltip>
        )}
        {filterable && onFilterOnlyValue !== undefined && (
          <Tooltip content={t('search.drawerOnlyThisValue')}>
            <button
              type="button"
              aria-label={`${t('search.drawerOnlyThisValue')}: ${field}`}
              className="shrink-0 cursor-pointer p-0.5 text-faint opacity-0 transition-opacity group-hover/kv:opacity-100 hover:text-primary"
              onClick={() => onFilterOnlyValue(field, raw)}
            >
              <Crosshair className="size-3.5" aria-hidden />
            </button>
          </Tooltip>
        )}
        <Tooltip content={copyLabel}>
          <button
            type="button"
            aria-label={`${copyLabel}: ${k}`}
            className="shrink-0 cursor-pointer p-0.5 text-faint opacity-0 transition-opacity group-hover/kv:opacity-100 hover:text-fg"
            onClick={() => onCopy(raw)}
          >
            <Copy className="size-3" aria-hidden />
          </button>
        </Tooltip>
      </div>
    </>
  );
}

/** 资源属性 chip（ClickStack 式 key: value 药丸，点击复制）. */
function ResourceChips({ entries, onCopy }: { entries: [string, unknown][]; onCopy: (text: string) => void }) {
  const { t } = useTranslation();
  if (entries.length === 0) {
    return <span className="text-xs text-faint">-</span>;
  }

  return (
    <div className="flex flex-wrap gap-1.5">
      {entries.map(([key, value]) => (
        <Tooltip key={key} content={t('search.copyValue')}>
          <button
            type="button"
            className="max-w-full cursor-pointer truncate rounded-sm border border-border bg-surface-2 px-1.5 py-0.5 font-mono text-[11px] text-muted transition-colors hover:text-fg"
            onClick={() => onCopy(`${key}: ${String(value)}`)}
          >
            <span className="text-subtle">{key}: </span>
            {String(value)}
          </button>
        </Tooltip>
      ))}
    </div>
  );
}

/** 概览 tab：顶层属性 + 日志/Span 属性 + 事件 + 资源属性（Body 已上移至 tab 之外）. */
export function OverviewTab({
  kind,
  record,
  topSearch,
  attrSearch,
  onTopSearch,
  onAttrSearch,
  onCopy,
  onTraceActivate,
  onFilterByValue,
  onFilterOnlyValue,
}: {
  kind: 'log' | 'span';
  record: DetailRow;
  topSearch: string;
  attrSearch: string;
  onTopSearch: (next: string) => void;
  onAttrSearch: (next: string) => void;
  onCopy: (text: string) => void;
  onTraceActivate: (traceId: string) => void;
  onFilterByValue: (field: string, value: unknown) => void;
  onFilterOnlyValue: (field: string, value: unknown) => void;
}) {
  const { t } = useTranslation();
  const present = (...keys: string[]): [string, unknown][] =>
    keys
      .filter((key) => record[key] !== null && record[key] !== undefined && record[key] !== '')
      .map((key) => [key, record[key]] as [string, unknown]);
  const topLevelEntries = kind === 'log'
    ? present('ts', 'service', 'severity', 'source', 'span_id', 'trace_id')
    : present('ts', 'service', 'name', 'duration_us', 'kind', 'status', 'parent_span_id', 'span_id', 'trace_id');
  const logAttrEntries = useMemo(() => parseJsonObject(record.attributes), [record]);
  const resourceEntries = useMemo(() => parseJsonObject(record.resource), [record]);
  const events = useMemo(() => (kind === 'span' ? parseJsonEvents(record.events) : []), [kind, record]);

  if (kind === 'span') {
    const status = String(record.status ?? '');
    return (
      <>
        <Section title={t('search.sectionSpanSummary')} raw={typeof record.name === 'string' ? record.name : undefined} onCopy={onCopy}>
          <div className="flex flex-wrap items-center gap-2 rounded-md border border-border bg-bg px-2.5 py-2">
            <span className="min-w-0 flex-1 font-mono text-sm break-all text-fg">{String(record.name ?? '-')}</span>
            {status.length > 0 && <Tag tone={STATUS_TONE[status] ?? 'default'}>{status}</Tag>}
            <span className="font-mono text-xs tabular-nums text-muted">{renderDetailValue('duration_us', record.duration_us)}</span>
          </div>
        </Section>
        <Section title={t('search.sectionTopLevel')}>
          <SearchBox value={topSearch} onChange={onTopSearch} />
          <KeyValueList
            entries={topLevelEntries}
            search={topSearch}
            onCopy={onCopy}
            onFilterByValue={onFilterByValue}
            onFilterOnlyValue={onFilterOnlyValue}
            onTraceActivate={onTraceActivate}
          />
        </Section>
        <Section title={t('search.sectionSpanAttributes')} raw={typeof record.attributes === 'string' ? record.attributes : undefined} onCopy={onCopy}>
          <SearchBox value={attrSearch} onChange={onAttrSearch} />
          <KeyValueList
            entries={logAttrEntries}
            search={attrSearch}
            onCopy={onCopy}
            onFilterByValue={onFilterByValue}
            onFilterOnlyValue={onFilterOnlyValue}
            fieldPrefix="attributes."
          />
        </Section>
        <Section title={t('search.sectionEvents')} raw={typeof record.events === 'string' ? record.events : undefined} onCopy={onCopy}>
          {events.length === 0 ? (
            <span className="text-xs text-faint">-</span>
          ) : (
            <div className="flex flex-col gap-1.5">
              {events.map((event, index) => (
                <div key={index} className="rounded-md border border-border bg-bg px-2.5 py-2">
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-mono text-xs font-medium text-fg">{String(event.name ?? '-')}</span>
                    <span className="font-mono text-[11px] tabular-nums text-subtle">
                      {typeof event.timestamp === 'string' && event.timestamp.length > 0 ? formatDateTimeMs(event.timestamp) : ''}
                    </span>
                  </div>
                  {event.attributes !== undefined && event.attributes !== null && Object.keys(event.attributes as object).length > 0 && (
                    <pre className="mt-1 font-mono text-[11px] break-all whitespace-pre-wrap text-muted">{prettyJson(event.attributes)}</pre>
                  )}
                </div>
              ))}
            </div>
          )}
        </Section>
        <Section title={t('search.sectionResourceAttributes')} raw={typeof record.resource === 'string' ? record.resource : undefined} onCopy={onCopy}>
          <ResourceChips entries={resourceEntries} onCopy={onCopy} />
        </Section>
      </>
    );
  }

  return (
    <>
      <Section title={t('search.sectionTopLevel')}>
        <SearchBox value={topSearch} onChange={onTopSearch} />
        <KeyValueList
          entries={topLevelEntries}
          search={topSearch}
          onCopy={onCopy}
          onFilterByValue={onFilterByValue}
          onFilterOnlyValue={onFilterOnlyValue}
          onTraceActivate={onTraceActivate}
        />
      </Section>
      <Section title={t('search.sectionLogAttributes')} raw={typeof record.attributes === 'string' ? record.attributes : undefined} onCopy={onCopy}>
        <SearchBox value={attrSearch} onChange={onAttrSearch} />
        <KeyValueList
          entries={logAttrEntries}
          search={attrSearch}
          onCopy={onCopy}
          onFilterByValue={onFilterByValue}
          onFilterOnlyValue={onFilterOnlyValue}
          fieldPrefix="attributes."
        />
      </Section>
      <Section title={t('search.sectionResourceAttributes')} raw={typeof record.resource === 'string' ? record.resource : undefined} onCopy={onCopy}>
        <ResourceChips entries={resourceEntries} onCopy={onCopy} />
      </Section>
    </>
  );
}

/** 递归过滤 JSON 结构：保留键名或后代叶子值命中关键字的分支（空关键字原样返回，无命中返回 undefined）. */
function filterJsonNode(value: unknown, text: string): unknown {
  if (text.length === 0) {
    return value;
  }

  if (Array.isArray(value)) {
    const items = value.map((item) => filterJsonNode(item, text)).filter((item) => item !== undefined);
    return items.length > 0 ? items : undefined;
  }

  if (value !== null && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(value)) {
      if (key.toLowerCase().includes(text)) {
        out[key] = child;
        continue;
      }

      const filtered = filterJsonNode(child, text);
      if (filtered !== undefined) {
        out[key] = filtered;
      }
    }

    return Object.keys(out).length > 0 ? out : undefined;
  }

  return String(value).toLowerCase().includes(text) ? value : undefined;
}

/** 列值 tab：整条记录渲染为单棵 JSON 结构树（attributes/resource/events 解析为嵌套对象），记录级搜索按分支过滤 + 复制整行 JSON. */
export function ValuesTab({ record, onCopy }: { record: DetailRow; onCopy: (text: string) => void }) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  // JSON 字符串列解析为嵌套对象，树中直接展示结构而非双重编码文本
  const display = useMemo(() => {
    const out: Record<string, unknown> = { ...record };
    for (const field of ['attributes', 'resource', 'events']) {
      const parsed = parseJsonContainer(out[field]);
      if (parsed !== undefined) {
        out[field] = parsed;
      }
    }

    return out;
  }, [record]);
  const filtered = useMemo(() => {
    const text = search.trim().toLowerCase();
    return (filterJsonNode(display, text) ?? {}) as Record<string, unknown>;
  }, [display, search]);

  return (
    <>
      <div className="flex items-center gap-2">
        <div className="min-w-0 flex-1">
          <SearchBox value={search} onChange={setSearch} />
        </div>
        <Tooltip content={t('search.drawerCopyRowJson')}>
          <Button size="sm" variant="secondary" aria-label={t('search.drawerCopyRowJson')} icon={<Copy className="size-3.5" aria-hidden />} onClick={() => onCopy(prettyJson(record))} />
        </Tooltip>
      </div>
      <div className="rounded-md border border-border bg-bg px-2.5 py-2">
        {Object.keys(filtered).length === 0 ? (
          <div className="text-xs text-faint">{t('search.attrNoMatch')}</div>
        ) : (
          <JsonTree value={filtered} defaultExpanded />
        )}
      </div>
    </>
  );
}

/** JSON 列解析为容器（对象/数组）；非 JSON 文本返回 undefined 按原文展示. */
function parseJsonContainer(value: unknown): Record<string, unknown> | unknown[] | undefined {
  if (value !== null && typeof value === 'object') {
    return value as Record<string, unknown> | unknown[];
  }

  if (typeof value === 'string' && value.length > 0) {
    try {
      const parsed: unknown = JSON.parse(value);
      if (parsed !== null && typeof parsed === 'object') {
        return parsed as Record<string, unknown> | unknown[];
      }
    } catch {
      // 非 JSON 文本按原文展示
    }
  }

  return undefined;
}

/** 按表格行身份条件补查完整记录（日志走 logs 表全列，span 走 traces 表全列；查不到/出错回退行数据）. */
async function fetchRecordByRow(target: DetailRow, targetDomain: SearchDomain, tsValue: string): Promise<DetailRow | null> {
  const service = typeof target.service === 'string' && target.service.length > 0 ? target.service : null;
  if (service === null) {
    return null;
  }

  // 参数绑定：$ts 以服务端序列化原样字符串回传（DuckDB CAST 回 TIMESTAMP 保持等值）
  const parameters: Record<string, string> = { ts: tsValue, service };
  const conditions = ['ts = $ts', 'service = $service'];
  for (const field of targetDomain === 'logs' ? ['severity', 'trace_id', 'span_id'] : ['trace_id', 'span_id']) {
    const value = target[field];
    if (typeof value === 'string' && value.length > 0) {
      parameters[field] = value;
      conditions.push(`${field} = $${field}`);
    }
  }

  if (targetDomain === 'logs' && target.id != null) {
    parameters.id = String(target.id);
    conditions.push('id = CAST($id AS BIGINT)');
  }
  const tsDayjs = dayjs(tsValue);
  const columns = targetDomain === 'logs'
    ? 'id, ts, service, severity, body, trace_id, span_id, source, attributes, resource'
    : 'trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource';
  const response = await searchSql({
    sql: `SELECT ${columns}\nFROM {{${targetDomain}}}\nWHERE ${conditions.join(' AND ')}\nLIMIT 1`,
    from: tsDayjs.startOf('day').toISOString(),
    to: tsDayjs.endOf('day').add(1, 'millisecond').toISOString(),
    rowLimit: 1,
    parameters,
  });

  if (response.rows.length === 0) {
    return null;
  }

  return Object.fromEntries(response.columns.map((name, index) => [name, response.rows[0][index]]));
}
