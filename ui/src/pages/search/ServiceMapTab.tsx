import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { Chart, Empty, SpinArea, Tag } from '@/design-system';
import { getTrace } from '@/api/traces';
import type { SpanItem } from '@/api/types';
import { formatDurationUs } from '@/utils/format';
import { cn } from '@/utils/cn';
import { ErrorHint } from './detailShared';

/** 服务节点聚合指标. */
export interface ServiceGraphNode {
  service: string;
  spans: number;
  errors: number;
  totalDurationUs: number;
}

/** 服务调用边聚合指标（from → to 为 parent → child 服务）. */
export interface ServiceGraphEdge {
  from: string;
  to: string;
  calls: number;
  errors: number;
  totalDurationUs: number;
}

/** trace 内服务拓扑（节点/边均按确定性规则排序，便于断言与稳定渲染）. */
export interface ServiceGraph {
  nodes: ServiceGraphNode[];
  edges: ServiceGraphEdge[];
}

/**
 * span 列表 → 服务拓扑聚合：span_id→service 映射，边 = parentService→childService；
 * 同服务内部调用不成边（只计入节点 span 数）。
 */
export function buildServiceGraph(spans: SpanItem[]): ServiceGraph {
  const spanService = new Map<string, string>();
  const nodes = new Map<string, ServiceGraphNode>();
  for (const span of spans) {
    if (!span.service || !span.spanId) {
      continue;
    }

    spanService.set(span.spanId, span.service);
    const node = nodes.get(span.service) ?? { service: span.service, spans: 0, errors: 0, totalDurationUs: 0 };
    node.spans += 1;
    if (span.status === 'error') {
      node.errors += 1;
    }

    node.totalDurationUs += typeof span.durationUs === 'number' ? span.durationUs : 0;
    nodes.set(span.service, node);
  }

  const edges = new Map<string, ServiceGraphEdge>();
  for (const span of spans) {
    const parentSpanId = typeof span.parentSpanId === 'string' ? span.parentSpanId : '';
    if (parentSpanId.length === 0) {
      continue;
    }

    const childService = spanService.get(span.spanId);
    const parentService = spanService.get(parentSpanId);
    // 未知父 span 或同服务内部调用不成边
    if (!childService || !parentService || childService === parentService) {
      continue;
    }

    const edgeKey = `${parentService}->${childService}`;
    const edge = edges.get(edgeKey) ?? { from: parentService, to: childService, calls: 0, errors: 0, totalDurationUs: 0 };
    edge.calls += 1;
    if (span.status === 'error') {
      edge.errors += 1;
    }

    edge.totalDurationUs += typeof span.durationUs === 'number' ? span.durationUs : 0;
    edges.set(edgeKey, edge);
  }

  return {
    nodes: [...nodes.values()].sort((a, b) => b.spans - a.spans || a.service.localeCompare(b.service)),
    edges: [...edges.values()].sort((a, b) => b.calls - a.calls || a.from.localeCompare(b.from) || a.to.localeCompare(b.to)),
  };
}

/** 着色维度（HyperDX 同款：延迟/错误率/吞吐量）. */
type ColorMode = 'latency' | 'errorRate' | 'throughput';

const ERROR_RATE_COLORS = { none: '#9ca3af', low: '#f87171', high: '#b91c1c' } as const;
const LATENCY_COLORS = { fast: '#16a34a', medium: '#d97706', slow: '#dc2626' } as const;
const THROUGHPUT_SCALE = ['#bfdbfe', '#93c5fd', '#60a5fa', '#3b82f6', '#2563eb', '#1d4ed8'] as const;

/** 错误率分档色（none → <5% → ≥5%，HyperDX 同阈值）. */
function errorRateColor(rate: number): string {
  if (rate <= 0) {
    return ERROR_RATE_COLORS.none;
  }

  return rate < 0.05 ? ERROR_RATE_COLORS.low : ERROR_RATE_COLORS.high;
}

/** 平均耗时分档色（<100ms / <1s / ≥1s）. */
function latencyColor(avgUs: number): string {
  if (avgUs < 100_000) {
    return LATENCY_COLORS.fast;
  }

  return avgUs < 1_000_000 ? LATENCY_COLORS.medium : LATENCY_COLORS.slow;
}

/** 吞吐量（span 占比）→ 蓝阶渐变. */
function throughputColor(spans: number, maxSpans: number): string {
  const ratio = maxSpans > 0 ? spans / maxSpans : 0;
  return THROUGHPUT_SCALE[Math.min(THROUGHPUT_SCALE.length - 1, Math.floor(ratio * THROUGHPUT_SCALE.length))];
}

/** 节点大小（span 数占比）→ 30~60px. */
function nodeSize(spans: number, maxSpans: number): number {
  const ratio = maxSpans > 0 ? spans / maxSpans : 0;
  return 30 + Math.round(ratio * 30);
}

/** 图例色块行. */
function LegendSwatches({ items }: { items: { color: string; label: string }[] }) {
  return (
    <div className="flex items-center gap-1.5">
      {items.map((item) => (
        <span key={item.label} className="flex items-center gap-1 text-[11px] text-subtle">
          <span className="inline-block size-2.5 rounded-full" style={{ backgroundColor: item.color }} aria-hidden />
          {item.label}
        </span>
      ))}
    </div>
  );
}

/**
 * Service Map tab（HyperDX 式）：当前 trace 的服务调用拓扑图。
 * 数据懒加载自 GET /api/traces/{traceId}；节点大小=span 数，着色按延迟/错误率/吞吐量切换。
 */
export function ServiceMapTab({ traceId, items }: { traceId: string; items?: SpanItem[] }) {
  const { t } = useTranslation();
  const [spans, setSpans] = useState<SpanItem[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const [mode, setMode] = useState<ColorMode>('errorRate');

  // 懒加载：tab 内容挂载（value=traceId 变化经 key 重挂载）时拉全 trace spans
  useEffect(() => {
    if (items !== undefined) {
      setSpans(items);
      setLoading(false);
      setError(null);
      return;
    }
    if (traceId.length === 0) {
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);
    getTrace(traceId)
      .then((data) => {
        if (!cancelled) setSpans(data);
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [traceId, nonce, items]);

  const graph = useMemo(() => buildServiceGraph(spans ?? []), [spans]);

  const option = useMemo<EChartsOption>(() => {
    const maxSpans = Math.max(1, ...graph.nodes.map((node) => node.spans));
    const maxCalls = Math.max(1, ...graph.edges.map((edge) => edge.calls));
    return {
      tooltip: {
        trigger: 'item',
        formatter: (params: unknown) => {
          const { dataType, data } = params as { dataType: string; data: Record<string, unknown> };
          if (dataType === 'edge') {
            const rate = Number(data.calls) > 0 ? Number(data.errors) / Number(data.calls) : 0;
            return [
              `<b>${String(data.from)} → ${String(data.to)}</b>`,
              `${t('search.smCalls')}: ${String(data.calls)}`,
              `${t('search.smErrorRate')}: ${(rate * 100).toFixed(1)}%`,
              `${t('search.smAvgDuration')}: ${formatDurationUs(Number(data.totalDurationUs) / Math.max(1, Number(data.calls)))}`,
            ].join('<br/>');
          }

          const rate = Number(data.spans) > 0 ? Number(data.errors) / Number(data.spans) : 0;
          return [
            `<b>${String(data.name)}</b>`,
            `spans: ${String(data.spans)}`,
            `${t('search.smErrorRate')}: ${(rate * 100).toFixed(1)}%`,
            `${t('search.smAvgDuration')}: ${formatDurationUs(Number(data.totalDurationUs) / Math.max(1, Number(data.spans)))}`,
          ].join('<br/>');
        },
      },
      series: [
        {
          type: 'graph',
          layout: 'force',
          roam: true,
          data: graph.nodes.map((node) => {
            const rate = node.spans > 0 ? node.errors / node.spans : 0;
            const avgUs = node.spans > 0 ? node.totalDurationUs / node.spans : 0;
            const color = mode === 'latency' ? latencyColor(avgUs) : mode === 'errorRate' ? errorRateColor(rate) : throughputColor(node.spans, maxSpans);
            return {
              id: node.service,
              name: node.service,
              spans: node.spans,
              errors: node.errors,
              totalDurationUs: node.totalDurationUs,
              symbolSize: nodeSize(node.spans, maxSpans),
              itemStyle: { color },
              label: { show: true, position: 'bottom', fontSize: 12 },
            };
          }),
          links: graph.edges.map((edge) => ({
            source: edge.from,
            target: edge.to,
            calls: edge.calls,
            errors: edge.errors,
            totalDurationUs: edge.totalDurationUs,
            lineStyle: { width: 1 + Math.round((edge.calls / maxCalls) * 3), color: '#94a3b8', curveness: 0.1 },
          })),
          force: { repulsion: 360, edgeLength: 140, gravity: 0.08 },
          draggable: true,
          edgeSymbol: ['none', 'arrow'],
          edgeSymbolSize: 9,
        },
      ],
    };
  }, [graph, mode, t]);

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <h4 className="text-xs font-medium text-subtle">{t('search.drawerTabServiceMap')}</h4>
        <Tag tone="default">Beta</Tag>
      </div>
      {/* 着色切换 + 图例 */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-1.5">
          {(
            [
              ['latency', t('search.smColorLatency')],
              ['errorRate', t('search.smColorErrorRate')],
              ['throughput', t('search.smColorThroughput')],
            ] as [ColorMode, string][]
          ).map(([value, label]) => (
            <button
              key={value}
              type="button"
              className={cn(
                'cursor-pointer rounded-sm border px-2 py-0.5 text-xs transition-colors',
                mode === value ? 'border-primary bg-primary-soft text-primary' : 'border-border text-muted hover:text-fg',
              )}
              onClick={() => setMode(value)}
            >
              {label}
            </button>
          ))}
        </div>
        <div className="flex flex-col items-end gap-1">
          {mode === 'latency' && (
            <LegendSwatches
              items={[
                { color: LATENCY_COLORS.fast, label: '<100ms' },
                { color: LATENCY_COLORS.medium, label: '<1s' },
                { color: LATENCY_COLORS.slow, label: '≥1s' },
              ]}
            />
          )}
          {mode === 'errorRate' && (
            <LegendSwatches
              items={[
                { color: ERROR_RATE_COLORS.none, label: t('search.smLegendErrorNone') },
                { color: ERROR_RATE_COLORS.low, label: '<5%' },
                { color: ERROR_RATE_COLORS.high, label: '≥5%' },
              ]}
            />
          )}
          {mode === 'throughput' && (
            <LegendSwatches
              items={[
                { color: THROUGHPUT_SCALE[0], label: t('search.smLegendFew') },
                { color: THROUGHPUT_SCALE[THROUGHPUT_SCALE.length - 1], label: t('search.smLegendMany') },
              ]}
            />
          )}
          <span className="text-[11px] text-faint">{t('search.smLegendNodeSize')}</span>
        </div>
      </div>
      {loading && <SpinArea />}
      {error !== null && <ErrorHint message={error} onRetry={() => setNonce((tick) => tick + 1)} />}
      {spans !== null && graph.nodes.length === 0 && !loading && <Empty description={t('search.smNoTraceData')} />}
      {graph.nodes.length > 0 && (
        <div className="rounded-md border border-border bg-bg">
          <Chart option={option} style={{ height: 320 }} />
        </div>
      )}
    </div>
  );
}
