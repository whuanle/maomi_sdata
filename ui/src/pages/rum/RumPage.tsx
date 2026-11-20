import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { fetchRumPerformance, fetchRumErrors, fetchRumReplay, fetchRumHeatmap, fetchRumSitemap } from '@/api/rum';
import type { RumReplayChunk, RumHeatmapCell, RumClickTarget, RumLiveClick, RumPageNode } from '@/api/types';
import {
  Button,
  Chart,
  DataTable,
  Empty,
  Select,
  Tag,
  type TableColumn,
} from '@/design-system';
import { useAuthStore } from '@/store/auth';
import { formatDateTime } from '@/utils/format';
import { cn } from '@/utils/cn';

/** 近 24 小时默认查询窗口. */
const WINDOW_HOURS = 24;

/** 时间窗口（近 24h）. */
function queryWindow(): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to.getTime() - WINDOW_HOURS * 3600 * 1000);
  return { from: from.toISOString(), to: to.toISOString() };
}

/** 卡片容器（RUM 视图通用）. */
function Panel({
  title,
  extra,
  children,
  className,
}: {
  title?: React.ReactNode;
  extra?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={cn('flex min-w-0 flex-col rounded-lg border border-border bg-surface', className)}>
      {(title || extra) && (
        <div className="flex items-center justify-between gap-2 border-b border-border px-4 pt-2.5 pb-2">
          <span className="flex min-w-0 items-center gap-2 text-sm font-medium text-muted">{title}</span>
          {extra && <span className="flex shrink-0 items-center gap-2">{extra}</span>}
        </div>
      )}
      <div className="min-h-0 flex-1 px-4 py-2.5">{children}</div>
    </section>
  );
}

/** 数字卡（PV/UV/速率等）. */
function MetricCard({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <Panel>
      <div className="text-xs text-subtle">{label}</div>
      <div className="mt-1.5 font-mono text-[28px] leading-9 font-semibold">{value}</div>
    </Panel>
  );
}

export { ClientSessionsPage as RumPage } from './ClientSessionsPage';

/** 性能视图：PV/UV + Web Vitals p75 + 错误 Top. */
export function PerformanceView({ projectKey, range }: { projectKey: string; range: { from: string; to: string } }) {
  const { t } = useTranslation();
  const [failed, setFailed] = useState(false);
  const [data, setData] = useState<{ pv: number; uv: number; vitals: Record<string, number>; errors: Array<{ name: string; count: number }> } | null>(null);

  useEffect(() => {
    if (!projectKey) {
      return;
    }

    void (async () => {
      try {
        const [performance, errors] = await Promise.all([
          fetchRumPerformance({ projectKey, ...range }),
          fetchRumErrors({ projectKey, ...range, topN: 10 }),
        ]);
        setData({ pv: performance.pageViews, uv: performance.uniqueVisitors, vitals: performance.vitalP75, errors: errors.items });
      } catch {
        setFailed(true);
      }
    })();
  }, [projectKey, range.from, range.to]);

  if (failed) return <div role="alert" className="text-error">{t('clientSession.loadFailed')}</div>;

  if (!data) {
    return <Empty description={t('common.loading')} />;
  }

  return (
    <div className="grid grid-cols-4 gap-4 pb-4">
      <MetricCard label="PV" value={data.pv} />
      <MetricCard label="UV" value={data.uv} />
      <Panel title={t('rum.vitalsP75')} className="col-span-2">
        {Object.keys(data.vitals).length === 0 && <span className="text-sm text-subtle">{t('common.empty')}</span>}
        <span className="flex flex-wrap gap-1.5">
          {Object.entries(data.vitals).map(([name, value]) => (
            <Tag key={name} tone="info">{name}: {Math.round(value * 100) / 100}</Tag>
          ))}
        </span>
      </Panel>
      <Panel title={t('rum.errorTop')} className="col-span-4">
        {data.errors.length === 0 && <span className="text-sm text-subtle">{t('common.empty')}</span>}
        {data.errors.map((error) => (
          <div key={error.name} className="flex items-center justify-between border-b border-border/60 py-1">
            <span className="font-mono text-xs">{error.name}</span>
            <Tag>{error.count}</Tag>
          </div>
        ))}
      </Panel>
    </div>
  );
}

/** 回放视图：会话选择 → 分段拉取 → gzip 解码 → rrweb-player 播放（加载失败降级原始事件预览）. */
export function ReplayView({ projectKey, sessionId, range }: { projectKey: string; sessionId: string; range?: { from: string; to: string } }) {
  const { t } = useTranslation();
  const [selected, setSelected] = useState(sessionId);
  const [retry, setRetry] = useState(0);
  const [damaged, setDamaged] = useState(0);
  const [events, setEvents] = useState<Array<Record<string, unknown>>>([]);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'empty' | 'error' | 'player-unavailable'>('idle');
  const playerHostRef = useRef<HTMLDivElement>(null);

  // 外部（会话视图）指定会话时跟随
  useEffect(() => {
    setSelected(sessionId);
  }, [sessionId]);

  useEffect(() => {
    if (!selected || !projectKey) return;
    let cancelled = false;
    setEvents([]);
    setDamaged(0);
    void (async () => {
      setStatus('loading');
      try {
        const result = await fetchRumReplay(selected, { projectKey, ...(range ?? queryWindow()) });
        const decoded = await decodeChunks(result.chunks);
        if (cancelled) return;
        setEvents(decoded.events);
        setDamaged(decoded.damaged);
        setStatus(decoded.events.length > 0 ? 'ready' : decoded.damaged ? 'error' : 'empty');
      } catch {
        if (!cancelled) setStatus('error');
      }
    })();
    return () => { cancelled = true; };
  }, [projectKey, selected, range?.from, range?.to, retry]);

  // 事件就绪后挂载 rrweb-player（动态加载，失败降级显示原始事件）
  useEffect(() => {
    if (status !== 'ready' || events.length === 0 || !playerHostRef.current) {
      return;
    }

    let cancelled = false;
    const host = playerHostRef.current;
    let player: { $destroy?: () => void; pause?: () => void; $set?: (props: { width: number }) => void } | undefined;
    let observer: ResizeObserver | undefined;
    void (async () => {
      try {
        const [{ default: RRWebPlayer }] = await Promise.all([import('rrweb-player'), import('rrweb-player/dist/style.css')]);
        if (cancelled || !playerHostRef.current) {
          return;
        }

        playerHostRef.current.innerHTML = '';
        type PlayerProps = { events: unknown; width: number; height: number; autoPlay: boolean };
        const playerCtor = RRWebPlayer as unknown as new (options: { target: HTMLElement; props: PlayerProps }) => NonNullable<typeof player>;
        playerHostRef.current.innerHTML = '';
        player = new playerCtor({
          target: playerHostRef.current,
          props: {
            // 解码产物为宽松形状（分段损坏跳过后的拼接），运行时由 rrweb-player 自行消费
            events,
            width: Math.max(240, playerHostRef.current.clientWidth),
            height: Math.max(200, Math.min(560, window.innerHeight - 220)),
            autoPlay: true,
          },
        });
        observer = new ResizeObserver(() => player?.$set?.({ width: Math.max(240, host.clientWidth) }));
        observer.observe(host);
      } catch {
        if (!cancelled) setStatus('player-unavailable');
      }
    })();

    return () => {
      cancelled = true;
      observer?.disconnect();
      player?.pause?.();
      player?.$destroy?.();
      host.replaceChildren();
    };
  }, [status, events]);

  return (
    <div className="pb-4">
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <span className="font-mono text-xs">{selected}</span>
        {status === 'loading' && <Tag tone="info">{t('common.loading')}</Tag>}
        {status === 'empty' && <Tag>{t('rum.replayEmpty')}</Tag>}
        {status === 'error' && <><Tag tone="error">{t('rum.replayError')}</Tag><Button onClick={() => setRetry((value) => value + 1)}>{t('common.refresh')}</Button></>}
        {damaged > 0 && <Tag tone="warning">{t('clientSession.damagedChunks', { count: damaged })}</Tag>}
        {status === 'player-unavailable' && <Tag tone="warning">{t('rum.replayRawFallback')}</Tag>}
      </div>

      <div ref={playerHostRef} data-testid="replay-player" className="min-h-[200px]" />

      {status === 'player-unavailable' && (
        <Panel title={t('rum.replayRawTitle')}>
          <pre className="max-h-[300px] overflow-auto font-mono text-[11px]">{JSON.stringify(events.slice(0, 20), null, 2)}</pre>
        </Panel>
      )}
    </div>
  );
}

/** 解码回放分段：base64 → gzip 解压（DecompressionStream，按魔数判定）→ rrweb 事件数组按 seq 拼接. */
export async function decodeChunks(chunks: RumReplayChunk[]): Promise<{ events: Array<Record<string, unknown>>; damaged: number }> {
  let damaged = 0;
  const events: Array<Record<string, unknown>> = [];
  for (const chunk of [...chunks].sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp) || a.seq - b.seq)) {
    try {
      const binary = Uint8Array.from(atob(chunk.blobBase64), (char) => char.charCodeAt(0));
      let bytes = binary;
      // gzip 魔数 1f 8b；无 CompressionStream 时的回退分段是原始字节
      if (binary.length > 2 && binary[0] === 0x1f && binary[1] === 0x8b && typeof DecompressionStream !== 'undefined') {
        const stream = new Blob([binary]).stream().pipeThrough(new DecompressionStream('gzip'));
        bytes = new Uint8Array(await new Response(stream).arrayBuffer());
      }

      const parsed = JSON.parse(new TextDecoder().decode(bytes)) as Array<Record<string, unknown>>;
      if (!Array.isArray(parsed)) throw new Error('Invalid replay chunk');
      events.push(...parsed);
    } catch {
      damaged += 1;
    }
  }

  events.sort((a, b) => Number(a.timestamp ?? 0) - Number(b.timestamp ?? 0));
  return { events, damaged };
}

/** 热力视图（T615/T616）：echarts 点击密度热力 + 页面过滤 + 点击目标 TopN. */
export function HeatmapView({ projectKey, range }: { projectKey: string; range: { from: string; to: string } }) {
  const { t } = useTranslation();
  const [cells, setCells] = useState<RumHeatmapCell[]>([]);
  const [totalClicks, setTotalClicks] = useState(0);
  const [topTargets, setTopTargets] = useState<RumClickTarget[]>([]);
  const [pages, setPages] = useState<RumPageNode[]>([]);
  const [pagePath, setPagePath] = useState<string>('');
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);

  // 页面路径选项（供过滤下拉）
  useEffect(() => {
    if (!projectKey) {
      return;
    }

    void (async () => {
      try {
        const result = await fetchRumSitemap({ projectKey, ...range, limit: 100 });
        setPages(result.items);
      } catch {
        setPages([]);
      }
    })();
  }, [projectKey, range.from, range.to]);

  useEffect(() => {
    if (!projectKey) {
      return;
    }

    void (async () => {
      try {
        const result = await fetchRumHeatmap({ projectKey, ...range, gridCells: 40, pagePath: pagePath || undefined });
        setCells(result.cells ?? []);
        setTotalClicks(result.totalClicks ?? 0);
        setTopTargets(result.topTargets ?? []);
      } catch {
        setFailed(true);
      } finally {
        setLoaded(true);
      }
    })();
  }, [projectKey, pagePath, range.from, range.to]);

  const grid = 40;
  const max = Math.max(1, ...cells.map((x) => x.count));
  // ECharts 笛卡尔坐标系上的 heatmap 要求两个类目轴：栅格桶即类目，索引用 tooltip 还原为百分比
  const axisLabels = Array.from({ length: grid }, (_, index) => `${Math.round((index / grid) * 100)}%`);
  const percent = (index: number) => `${((((index + 0.5) / grid) * 100)).toFixed(1)}%`;
  const option: EChartsOption = {
    grid: { left: 46, right: 16, top: 12, bottom: 56 },
    tooltip: {
      position: 'top' as const,
      formatter: (params) => {
        const item = Array.isArray(params) ? params[0] : params;
        const value = item?.value;
        if (!Array.isArray(value) || value.length < 3) return '';
        const [x, y, count] = value.map(Number);
        return `x: ${percent(x)}\ny: ${percent(y)}\n${t('rum.clicks')}: ${count}`;
      },
    },
    xAxis: {
      type: 'category' as const,
      data: axisLabels,
      axisLabel: { interval: 9 }, // 每 10 桶显示一个刻度：0% / 25% / 50% / 75%
      splitArea: { show: false },
    },
    yAxis: {
      type: 'category' as const,
      data: axisLabels,
      inverse: true, // 页面顶部在图上方
      axisLabel: { interval: 9 },
      splitArea: { show: false },
    },
    visualMap: {
      min: 0,
      max,
      calculable: true,
      orient: 'horizontal' as const,
      left: 'center' as const,
      bottom: 0,
      inRange: { color: ['#bae7ff', '#40a9ff', '#fa8c16', '#cf1322'] },
    },
    series: [
      {
        type: 'heatmap' as const,
        // 栅格索引直接作为类目索引；噪点(count=0)不渲染
        data: cells.filter((cell) => cell.count > 0).map((cell) => [cell.x, cell.y, cell.count]),
      },
    ],
  };

  return (
    <Panel
      title={<span>{t('rum.tabHeatmap')}</span>}
      extra={(
        <>
          <Tag>{`${t('rum.totalClicks')}: ${totalClicks}`}</Tag>
          <Select
            className="min-w-[180px]"
            value={pagePath}
            onValueChange={setPagePath}
            options={pages.map((page) => ({ value: page.pagePath, label: page.pagePath }))}
            placeholder={t('rum.allPages')}
            testId="rum-heatmap-page"
            aria-label="filter page"
          />
        </>
      )}
    >
      {failed && <div role="alert" className="text-error">{t('clientSession.loadFailed')}</div>}
      {!loaded && <Empty description={t('common.loading')} />}
      {loaded && cells.length === 0 && <Empty description={t('rum.noClicks')} />}
      {loaded && cells.length > 0 && <Chart option={option} style={{ height: 360 }} />}

      {loaded && topTargets.length > 0 && (
        <div className="mt-3 rounded-lg border border-border px-3 py-2">
          <div className="pb-1 text-sm font-medium text-muted">{t('rum.topTargets')}</div>
          {topTargets.map((target) => (
            <div key={target.target} className="flex items-center justify-between border-b border-border/60 py-1">
              <span className="font-mono text-xs">{target.target}</span>
              <Tag>{target.count}</Tag>
            </div>
          ))}
        </div>
      )}
    </Panel>
  );
}

/** 实时视图（T616）：SignalR 订阅 /hub/rum，实时点击流 + 速率 + 会话内迷你热力. */
export function LiveView({ projectKey }: { projectKey: string }) {
  const { t } = useTranslation();
  const [status, setStatus] = useState<'connecting' | 'connected' | 'disconnected'>('connecting');
  const [clicks, setClicks] = useState<RumLiveClick[]>([]);
  const [received, setReceived] = useState(0);
  const [rate, setRate] = useState(0);
  const [miniCells, setMiniCells] = useState<Array<{ key: string; count: number }>>([]);
  const recentTimestampsRef = useRef<number[]>([]);
  const miniGridRef = useRef<Map<string, number>>(new Map());

  useEffect(() => {
    if (!projectKey || !useAuthStore.getState().token) {
      setStatus('disconnected');
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hub/rum', {
        transport: HttpTransportType.WebSockets,
        accessTokenFactory: () => useAuthStore.getState().token ?? '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    const rateTimer = setInterval(() => {
      const now = Date.now();
      recentTimestampsRef.current = recentTimestampsRef.current.filter((ts) => now - ts <= 60_000);
      setRate(recentTimestampsRef.current.length);
    }, 2000);

    connection.on('click', (click: RumLiveClick) => {
      setReceived((count) => count + 1);
      recentTimestampsRef.current.push(Date.now());
      setClicks((prev) => [click, ...prev].slice(0, 50));
      // 迷你热力：pageX/docW 归一化到 20x20（缺文档尺寸的旧探针跳过）
      if (click.docW && click.pageX != null) {
        const grid = 20;
        const gx = Math.min(grid - 1, Math.floor((grid * click.pageX) / click.docW));
        const gy = click.docH ? Math.min(grid - 1, Math.floor((grid * (click.pageY ?? 0)) / click.docH)) : 0;
        const key = `${gx},${gy}`;
        miniGridRef.current.set(key, (miniGridRef.current.get(key) ?? 0) + 1);
        setMiniCells(Array.from(miniGridRef.current, ([cellKey, count]) => ({ key: cellKey, count })));
      }
    });
    connection.onreconnecting(() => setStatus('connecting'));
    connection.onreconnected(() => { void connection.invoke('Subscribe', projectKey).then(() => setStatus('connected')).catch(() => setStatus('disconnected')); });
    connection.onclose(() => setStatus('disconnected'));

    setStatus('connecting');
    connection
      .start()
      .then(() => connection.invoke('Subscribe', projectKey))
      .then(() => setStatus('connected'))
      .catch(() => setStatus('disconnected'));

    return () => {
      clearInterval(rateTimer);
      void connection.invoke('Unsubscribe', projectKey).catch(() => undefined);
      void connection.stop().catch(() => undefined);
    };
  }, [projectKey]);

  const miniGrid = 20;
  const miniMax = Math.max(1, ...miniCells.map((cell) => cell.count));
  const statusBadge =
    status === 'connected'
      ? { dot: 'bg-success animate-pulse', text: t('rum.liveConnected') }
      : status === 'connecting'
        ? { dot: 'bg-warning', text: t('rum.liveConnecting') }
        : { dot: 'bg-error', text: t('rum.liveDisconnected') };

  const columns: TableColumn<RumLiveClick>[] = [
    { title: t('common.updateTime'), dataIndex: 'timestamp', width: 110, render: (value: string) => <span className="text-xs">{formatDateTime(value)}</span> },
    { title: t('rum.pagePath'), dataIndex: 'pagePath', width: 140, render: (value?: string) => value || '-' },
    { title: t('rum.clickTarget'), dataIndex: 'selector', render: (value?: string) => <span className="font-mono text-xs">{value || '-'}</span> },
    { title: 'X, Y', key: 'coord', width: 110, mono: true, render: (_value: unknown, record: RumLiveClick) => <span className="text-xs">{record.pageX ?? record.x ?? '-'}, {record.pageY ?? record.y ?? '-'}</span> },
  ];

  return (
    <div className="grid grid-cols-4 gap-4 pb-4">
      <Panel>
        <div className="text-xs text-subtle">{t('rum.liveStatus')}</div>
        <div className="mt-2 flex items-center gap-2">
          <span className={cn('size-2.5 rounded-full', statusBadge.dot)} aria-hidden />
          <span className="text-sm">{statusBadge.text}</span>
        </div>
      </Panel>
      <MetricCard label={t('rum.liveRate')} value={rate} />
      <MetricCard label={t('rum.liveReceived')} value={received} />
      <Panel title={t('rum.liveMiniHeatmap')}>
        <div className="grid gap-px" style={{ gridTemplateColumns: `repeat(${miniGrid}, 10px)` }}>
          {Array.from({ length: miniGrid * miniGrid }, (_, i) => {
            const gx = i % miniGrid;
            const gy = Math.floor(i / miniGrid);
            const cell = miniCells.find((item) => item.key === `${gx},${gy}`);
            return (
              <div
                key={i}
                title={cell ? `${gx},${gy}: ${cell.count}` : `${gx},${gy}`}
                className="size-2.5 rounded-[2px]"
                style={{ background: cell ? `rgba(240, 68, 56, ${0.15 + 0.85 * (cell.count / miniMax)})` : 'var(--c-surface-2)' }}
              />
            );
          })}
        </div>
      </Panel>
      <Panel title={t('rum.liveRecentClicks')} className="col-span-4">
        <DataTable<RumLiveClick> rowKey={(_, index) => String(index)} columns={columns} dataSource={clicks} pagination={{ pageSize: 10 }} />
      </Panel>
    </div>
  );
}

/** 站点地图视图（T615）：页面路径 × 点击/错误聚合表. */
export function SitemapView({ projectKey, range }: { projectKey: string; range: { from: string; to: string } }) {
  const { t } = useTranslation();
  const [pages, setPages] = useState<RumPageNode[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!projectKey) {
      return;
    }

    void (async () => {
      try {
        const result = await fetchRumSitemap({ projectKey, ...range, limit: 100 });
        setPages(result.items);
      } catch {
        setFailed(true);
      } finally {
        setLoaded(true);
      }
    })();
  }, [projectKey, range.from, range.to]);

  if (!loaded) {
    return <Empty description={t('common.loading')} />;
  }

  if (failed) return <div role="alert" className="text-error">{t('clientSession.loadFailed')}</div>;

  return (
    <Panel title={t('rum.tabSitemap')} className="mb-4">
      <table className="w-full border-collapse text-xs">
        <thead>
          <tr className="text-subtle">
            <th className="px-1 py-1.5 text-left font-medium">{t('rum.pagePath')}</th>
            <th className="px-1 py-1.5 text-right font-medium">{t('rum.clicks')}</th>
            <th className="px-1 py-1.5 text-right font-medium">{t('rum.errors')}</th>
          </tr>
        </thead>
        <tbody>
          {pages.map((page) => (
            <tr key={page.pagePath} className="border-t border-border/60">
              <td className="px-1 py-1.5 font-mono">{page.pagePath}</td>
              <td className="px-1 py-1.5 text-right font-mono">{page.clicks}</td>
              <td className={cn('px-1 py-1.5 text-right font-mono', page.errors > 0 && 'text-error')}>{page.errors}</td>
            </tr>
          ))}
          {pages.length === 0 && (
            <tr><td colSpan={3} className="px-1 py-1.5 text-subtle">{t('rum.noClicks')}</td></tr>
          )}
        </tbody>
      </table>
    </Panel>
  );
}
