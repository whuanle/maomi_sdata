import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { ChevronDown, ChevronRight, RotateCcw, ZoomIn, ZoomOut } from 'lucide-react';
import { Button, Chart, Checkbox } from '@/design-system';
import type { TraceRow } from '@/api/traceSearch';
import { cn } from '@/utils/cn';
import { formatDurationUs } from '@/utils/format';
import { buildTraceTree, spanEvents, timestampUs } from './traceTimeline';
import { useCssTheme } from '@/design-system/theme';

const SERVICE_COLORS = ['#4385f5', '#e9ac18', '#34a86b', '#a477df', '#e77f50', '#36a9ba'];
const ROW_HEIGHT = 28;
const VISIBLE_ROWS = 18;
type Marker = { value: number[]; tooltipText: string; span?: TraceRow; log?: TraceRow; event?: ReturnType<typeof spanEvents>[number] };

/** 调用树和时间轴共用可视行；overview 控制实际时间视窗。 */
export function TraceWaterfall({ spans, logs, selectedId, onSelect, onLogSelect, onEventSelect }: {
  spans: TraceRow[]; logs: TraceRow[]; selectedId: string;
  onSelect: (row: TraceRow) => void; onLogSelect: (row: TraceRow) => void;
  onEventSelect?: (event: ReturnType<typeof spanEvents>[number]) => void;
}) {
  const { t } = useTranslation();
  const theme = useCssTheme();
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [range, setRange] = useState<[number, number]>([0, 100]);
  const [firstRow, setFirstRow] = useState(0);
  const [showSpans, setShowSpans] = useState(true);
  const [showLogs, setShowLogs] = useState(true);
  const [showEvents, setShowEvents] = useState(true);
  const viewportRef = useRef<HTMLDivElement>(null);
  const wheelZoomRef = useRef<(factor: number) => void>(() => undefined);
  useEffect(() => {
    const element = viewportRef.current;
    if (!element) return;
    const wheel = (event: WheelEvent) => {
      if (!event.ctrlKey && !event.metaKey) return;
      event.preventDefault();
      wheelZoomRef.current(event.deltaY > 0 ? 1.25 : 0.8);
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => element.removeEventListener('wheel', wheel);
  }, [spans.length]);
  const validSpans = useMemo(() => spans.filter((span) => Number.isFinite(timestampUs(span.ts))), [spans]);
  const tree = useMemo(() => buildTraceTree(validSpans), [validSpans]);
  const children = useMemo(() => {
    const result = new Map<string, number>();
    tree.rows.forEach((row) => { if (row.parentId) result.set(row.parentId, (result.get(row.parentId) ?? 0) + 1); });
    return result;
  }, [tree]);
  const visible = useMemo(() => {
    const hidden = new Set<string>();
    return tree.rows.filter((row) => {
      const id = String(row.span.span_id);
      if (row.parentId && (collapsed.has(row.parentId) || hidden.has(row.parentId))) { hidden.add(id); return false; }
      return true;
    });
  }, [tree, collapsed]);
  const allIds = useMemo(() => new Set(validSpans.map((span) => String(span.span_id))), [validSpans]);
  const unmatched = logs.filter((log) => !allIds.has(String(log.span_id ?? '')));
  const rows = [...visible.map((row) => ({ ...row, unmatched: false })), ...(unmatched.length && showLogs ? [{ span: {} as TraceRow, depth: 0, parentId: null, unmatched: true }] : [])];
  const pageStart = Math.min(firstRow, Math.max(0, rows.length - VISIBLE_ROWS));
  const page = rows.slice(pageStart, pageStart + VISIBLE_ROWS);
  const markerTimes = [...logs.map((log) => timestampUs(log.ts)), ...validSpans.flatMap((span) => spanEvents(span.events).map((event) => timestampUs(event.timestamp)))].filter(Number.isFinite);
  const spanStart = validSpans.reduce((value, span) => Math.min(value, timestampUs(span.ts)), Infinity);
  const start = Number.isFinite(spanStart) ? markerTimes.reduce((value, ts) => Math.min(value, ts), spanStart) : 0;
  const durationUs = (span: TraceRow) => Number.isFinite(Number(span.duration_us)) ? Math.max(0, Number(span.duration_us)) : 0;
  const spanEnd = validSpans.reduce((value, span) => Math.max(value, timestampUs(span.ts) + durationUs(span)), start + 1);
  const end = markerTimes.reduce((value, ts) => Math.max(value, ts), spanEnd);
  const duration = Math.max(end - start, 1);
  const services = [...new Set(validSpans.map((span) => String(span.service)))].sort();
  const color = (span: TraceRow) => String(span.status).toLowerCase() === 'error' ? '#dc4545' : SERVICE_COLORS[services.indexOf(String(span.service)) % SERVICE_COLORS.length];
  const min = duration * range[0] / 100;
  const max = duration * range[1] / 100;
  const zoom = (factor: number) => {
    const center = (range[0] + range[1]) / 2;
    const width = Math.max(0.01, Math.min(100, (range[1] - range[0]) * factor));
    const left = Math.max(0, Math.min(100 - width, center - width / 2));
    setRange([left, left + width]);
  };
  useEffect(() => { wheelZoomRef.current = zoom; });
  const spanData: Marker[] = page.flatMap((row, index) => row.unmatched ? [] : [{
    value: [index, timestampUs(row.span.ts) - start, timestampUs(row.span.ts) - start + durationUs(row.span)],
    tooltipText: String(row.span.service) + ' · ' + String(row.span.name) + '\n' + formatDurationUs(Number(row.span.duration_us)),
    span: row.span,
  }]);
  const logData: Marker[] = logs.flatMap((log) => {
    const index = page.findIndex((row) => row.unmatched ? !allIds.has(String(log.span_id ?? '')) : row.span.span_id === log.span_id);
    const ts = timestampUs(log.ts);
    return index < 0 || !Number.isFinite(ts) ? [] : [{ value: [ts - start, index], tooltipText: String(log.severity ?? '') + ' · ' + String(log.body ?? ''), log }];
  });
  const eventData: Marker[] = page.flatMap((row, index) => row.unmatched ? [] : spanEvents(row.span.events).flatMap((event) => {
    const ts = timestampUs(event.timestamp);
    return Number.isFinite(ts) ? [{ value: [ts - start, index], tooltipText: event.name + '\n' + event.timestamp, event }] : [];
  }));
  const option: EChartsOption = {
    animation: false,
    grid: { left: 0, right: '28%', top: 28, bottom: 0 },
    xAxis: { type: 'value', min, max, position: 'top', axisLabel: { formatter: (value: number) => formatDurationUs(value), fontSize: 10, color: theme.textSecondary }, splitLine: { show: true, lineStyle: { color: theme.border } } },
    yAxis: { type: 'category', inverse: true, data: page.map((_, index) => String(index)), show: false },
    tooltip: { trigger: 'item', renderMode: 'richText', formatter: (raw: unknown) => (raw as { data?: Marker }).data?.tooltipText ?? '' },
    series: [
      { type: 'custom', clip: false, encode: { x: [1, 2], y: 0 }, data: showSpans ? spanData : [],
        renderItem: (_params, api) => {
          const index = Number(api.value(0));
          const span = page[index].span;
          const left = api.coord([Math.max(min, Number(api.value(1))), index]);
          const right = api.coord([Math.min(max, Number(api.value(2))), index]);
          if (Number(api.value(2)) < min || Number(api.value(1)) > max) return;
          const barWidth = Math.max(2, right[0] - left[0]);
          const labelX = Math.min(left[0] + barWidth + 4, api.getWidth() - 4);
          const labelWidth = Math.max(0, api.getWidth() - labelX - 4);
          const selected = String(span.span_id) === selectedId;
          // 右侧被视窗裁剪时箭头表示仍在继续，不能把可见边缘误认为真实结束。
          const continuation = Number(api.value(2)) > max ? '→ ' : '';
          return { type: 'group', children: [
            { type: 'rect', shape: { x: left[0], y: left[1] - 9, width: barWidth, height: 18 },
              style: { fill: color(span), opacity: selected ? 1 : 0.75, stroke: selected ? theme.text : undefined, lineWidth: 2 } },
            { type: 'text', style: { x: labelX, y: left[1], width: labelWidth,
              text: continuation + formatDurationUs(Number(span.duration_us)) + ' · ' + String(span.name ?? ''),
              overflow: 'truncate', ellipsis: '…', fill: selected ? theme.text : theme.textSecondary,
              font: selected ? 'bold 10px monospace' : '10px monospace', verticalAlign: 'middle' } },
          ] };
        } },
      { type: 'scatter', symbol: 'rect', symbolSize: [6, 16], data: showLogs ? logData : [], itemStyle: { color: '#34a86b' } },
      { type: 'scatter', symbol: 'diamond', symbolSize: 9, data: showEvents ? eventData : [], itemStyle: { color: '#a477df' } },
    ],
  };
  const overview: EChartsOption = {
    animation: false, grid: { left: 8, right: '28%', top: 18, bottom: 26 },
    xAxis: [{ type: 'value', min: 0, max: duration, position: 'top', axisLabel: { fontSize: 9, color: theme.textSecondary, formatter: (value: number) => formatDurationUs(value) }, splitLine: { show: false } }, { type: 'value', min: 0, max: duration, show: false }],
    yAxis: { type: 'category', inverse: true, data: validSpans.map((span) => String(span.span_id)), show: false },
    dataZoom: [{ type: 'slider', xAxisIndex: 1, disabled: false, start: range[0], end: range[1], bottom: 0, height: 16, showDetail: false, filterMode: 'none' }],
    series: [{ type: 'custom', encode: { x: [1, 2], y: 0 }, renderItem: (_params, api) => {
      const index = Number(api.value(0));
      const left = api.coord([api.value(1), index]);
      const right = api.coord([api.value(2), index]);
      return { type: 'rect', shape: { x: left[0], y: left[1], width: Math.max(2, right[0] - left[0]), height: 2 }, style: { fill: color(validSpans[index]) } };
    }, data: validSpans.map((span, index) => [index, timestampUs(span.ts) - start, timestampUs(span.ts) - start + durationUs(span)]) }],
  };
  if (!spans.length) return null;
  return <div className="flex min-w-0 flex-col gap-3" data-testid="trace-waterfall">
    <Chart option={overview} style={{ height: 88 }} onEvents={{ datazoom: (raw: unknown) => {
      const event = raw as { start?: number; end?: number; batch?: { start: number; end: number }[] };
      const next = event.batch?.[0] ?? event;
      if (Number.isFinite(next.start) && Number.isFinite(next.end) && next.end! > next.start!) setRange([next.start!, next.end!]);
    } }} />
    <div className="flex flex-wrap items-center gap-2 text-xs text-muted">
      <span className="font-mono">{t('search.traceCounts', { spans: spans.length, logs: logs.length, errors: spans.filter((span) => String(span.status).toLowerCase() === 'error').length })}</span>
      <Checkbox label={t('search.traceSpans')} checked={showSpans} onChange={(event) => setShowSpans(event.target.checked)} />
      <Checkbox label={t('search.drawerTabTraceLogs')} checked={showLogs} onChange={(event) => setShowLogs(event.target.checked)} />
      <Checkbox label={t('search.sectionEvents')} checked={showEvents} onChange={(event) => setShowEvents(event.target.checked)} />
      <Button size="sm" variant="link" icon={<ZoomIn />} onClick={() => zoom(0.5)}>{t('search.traceZoomIn')}</Button>
      <Button size="sm" variant="link" icon={<ZoomOut />} onClick={() => zoom(2)}>{t('search.traceZoomOut')}</Button>
      <Button size="sm" variant="link" icon={<RotateCcw />} onClick={() => { setRange([0, 100]); setCollapsed(new Set()); setFirstRow(0); }}>{t('search.traceReset')}</Button>
      <span>{t('search.traceZoomHint')}</span>
    </div>
    {tree.rows.length > visible.length && <p role="status" className="text-xs text-muted">{t('search.traceHiddenRecords', { count: tree.rows.length - visible.length })}</p>}
    <div className="flex flex-wrap gap-3 font-mono text-xs text-muted">{services.map((service) => <span key={service}>{service}</span>)}</div>
    <div ref={viewportRef} className="flex min-w-0 border border-border" onWheel={(event) => {
      if (!event.ctrlKey && !event.metaKey && rows.length > VISIBLE_ROWS && event.deltaY !== 0) setFirstRow((previous) => Math.max(0, Math.min(Math.max(0, rows.length - VISIBLE_ROWS), previous + (event.deltaY > 0 ? 3 : -3))));
    }}>
      <div className="w-64 shrink-0 bg-surface pt-7">
        {page.map(({ span, depth, unmatched: isUnmatched }) => {
          const id = String(span.span_id);
          return <div key={isUnmatched ? 'unmatched' : id} className={cn('flex h-7 items-center border-b border-border/30 text-xs', selectedId === id && 'bg-primary-soft')}>
            {children.has(id) && <button type="button" aria-label={t('search.traceToggleChildren', { name: String(span.name) })} aria-expanded={!collapsed.has(id)} onClick={() => setCollapsed((previous) => { const next = new Set(previous); if (next.has(id)) next.delete(id); else next.add(id); return next; })}>{collapsed.has(id) ? <ChevronRight className="size-3" /> : <ChevronDown className="size-3" />}</button>}
            <button type="button" className="min-w-0 flex-1 truncate px-1 text-left font-mono text-fg" aria-label={isUnmatched ? t('search.traceUnmatchedLogs') : String(span.service) + ' · ' + String(span.name)} title={String(span.name ?? t('search.traceUnmatchedLogs'))} onClick={() => { if (!isUnmatched) onSelect(span); }}>
              {'│ '.repeat(Math.min(depth, 8))}{isUnmatched ? t('search.traceUnmatchedLogs') + ' (' + unmatched.length + ')' : String(span.service) + ' · ' + String(span.name)}
            </button>
            {collapsed.has(id) && <span className="text-faint">+{children.get(id)}</span>}
            {!isUnmatched && <span className="shrink-0 px-1 font-mono text-subtle" title={String(span.duration_us) + ' µs'}>{formatDurationUs(Number(span.duration_us))}</span>}
          </div>;
        })}
      </div>
      <div className="min-w-0 flex-1"><Chart option={option} style={{ height: Math.max(1, page.length) * ROW_HEIGHT + 28 }} onEvents={{ click: (raw: unknown) => {
        const data = (raw as { data?: Marker }).data;
        if (data?.span) onSelect(data.span);
        if (data?.log) onLogSelect(data.log);
        if (data?.event) onEventSelect?.(data.event);
      } }} /></div>
    </div>
    {rows.length > VISIBLE_ROWS && <input type="range" className="w-full accent-primary" aria-label={t('search.traceRows')} min={0} max={Math.max(0, rows.length - VISIBLE_ROWS)} value={Math.min(pageStart, Math.max(0, rows.length - VISIBLE_ROWS))} onChange={(event) => setFirstRow(Number(event.target.value))} />}
  </div>;
}
