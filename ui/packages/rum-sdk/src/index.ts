/**
 * SData RUM SDK（阶段 A，T612；回放 T614；点击增强/实时通道 T616）：
 * JS 错误 + PV/SPA 路由 + fetch/XHR 计时 + PerformanceObserver vitals + 可插拔 rrweb 会话回放
 * → 批量队列 → sendBeacon/fetch 上报 /rum/v1/beacon；
 * 点击事件可选 SignalR 实时直连（realtime: true，断线回落批量通道）.
 */

import { ReplaySession, type ReplayRecorderFactory } from './replay';
import { createRealtimeChannel, type ConnectionFactory, type RealtimeChannel } from './realtime';

export type { ConnectionFactory, RealtimeChannel, RealtimeClickPayload } from './realtime';

export interface RumSdkOptions {
  /** SData 服务地址（同源可省略为 ''，跨域需完整 origin）. */
  serverUrl?: string;
  /** RUM 项目标识（C3 projectKey，形如 rum_xxx）. */
  projectKey: string;
  /** 批量上报阈值（默认 20）. */
  batchSize?: number;
  /** 定时上报间隔 ms（默认 5000）. */
  flushIntervalMs?: number;
  /** 失败重试上限（默认 3，超限丢弃并停止重试）. */
  maxRetries?: number;
  /** 回放录制器工厂（T614：宿主注入 rrweb record；缺省不录制）. */
  replayRecorderFactory?: ReplayRecorderFactory;
  /** 回放分段事件条数（默认 50）. */
  replayChunkEvents?: number;
  /** 回放分段定时 ms（默认 5000）. */
  replayChunkIntervalMs?: number;
  /** 启用点击 SignalR 实时通道（默认 false；断线自动回落批量通道，T616）. */
  realtime?: boolean;
  /** 实时连接工厂（测试/宿主自定义传输注入）. */
  realtimeConnectionFactory?: ConnectionFactory;
}

export interface RumEvent {
  type: 'pageview' | 'vital' | 'error' | 'click' | 'custom' | 'replay-chunk';
  name?: string;
  pagePath?: string;
  value?: number;
  ts?: string;
  [key: string]: unknown;
}

export interface RumConfig {
  perfSampleRate: number;
  replaySampleRate: number;
}

const DEFAULT_BATCH_SIZE = 20;
const DEFAULT_FLUSH_INTERVAL_MS = 5000;
const DEFAULT_MAX_RETRIES = 3;

let batchQueue: RumEvent[] = [];
let retryQueue: RumEvent[] = [];
let retryCount = 0;
let flushTimer: ReturnType<typeof setTimeout> | undefined;
let perfSampleRate = 1;
let started = false;
let options: RumSdkOptions | undefined;
let replaySession: ReplaySession | undefined;
let realtimeChannel: RealtimeChannel | undefined;

const SESSION_STORAGE_KEY = 'sdata_rum_session_id';
let fallbackSessionId = '';

/** 会话标识（sessionStorage 每标签页一份；隐私模式回退内存 id）. */
export function sessionId(): string {
  try {
    const existing = window.sessionStorage?.getItem(SESSION_STORAGE_KEY);
    if (existing) {
      return existing;
    }

    const created = newId();
    window.sessionStorage?.setItem(SESSION_STORAGE_KEY, created);
    return created;
  } catch {
    fallbackSessionId ||= newId();
    return fallbackSessionId;
  }
}

function newId(): string {
  const c = typeof crypto !== 'undefined' ? crypto : undefined;
  return c?.randomUUID ? c.randomUUID() : `sid_${Date.now()}_${Math.random().toString(36).slice(2)}`;
}

/** 事件是否放行：error/pageview/replay-chunk 全采（回放采样在启动时判定）；perf 类按采样率. */
export function shouldSampleEvent(event: RumEvent): boolean {
  if (event.type === 'error' || event.type === 'pageview' || event.type === 'replay-chunk') {
    return true;
  }

  return Math.random() < perfSampleRate;
}

/** 构造事件（补全 pagePath/ts 缺省值）. */
export function buildEvent(event: Partial<RumEvent>): RumEvent {
  const globalObj = typeof window !== 'undefined' ? window : ({} as typeof window);
  const location = globalObj.location;
  return {
    ...event,
    type: event.type ?? 'custom',
    pagePath: event.pagePath ?? location?.pathname ?? '/',
    ts: event.ts ?? new Date().toISOString(),
  } as RumEvent;
}

/** 上报通道：sendBeacon 优先，fetch keepalive 回退. */
export async function transport(url: string, body: string): Promise<boolean> {
  const nav = typeof navigator !== 'undefined' ? navigator : undefined;
  if (nav?.sendBeacon) {
    if (nav.sendBeacon(url, new Blob([body], { type: 'application/json' }))) {
      return true;
    }
  }

  try {
    const response = await fetch(url, {
      method: 'POST',
      body,
      headers: { 'Content-Type': 'application/json' },
      keepalive: true,
    });
    return response.status === 204 || response.ok;
  } catch {
    return false;
  }
}

/** 队列当前条数（测试用）. */
export function queueLength(): number {
  return batchQueue.length + retryQueue.length;
}

/**
 * 初始化并启动采集（SPA 单例；重复调用幂等）.
 */
export function init(sdkOptions: RumSdkOptions): void {
  if (started) {
    return;
  }

  options = sdkOptions;
  started = true;
  if (sdkOptions.realtime && sdkOptions.projectKey) {
    realtimeChannel = createRealtimeChannel({
      serverUrl: sdkOptions.serverUrl ?? '',
      projectKey: sdkOptions.projectKey,
      factory: sdkOptions.realtimeConnectionFactory,
    });
  }

  void loadConfig();
  installInstrumentation();
  scheduleFlush();
}

/** 卸载（测试/热更新用）：移除埋点监听、还原被 patch 的方法并清空状态. */
export function reset(): void {
  if (flushTimer) {
    clearTimeout(flushTimer);
    flushTimer = undefined;
  }

  uninstallInstrumentation();
  void replaySession?.stop();
  replaySession = undefined;
  void realtimeChannel?.stop();
  realtimeChannel = undefined;
  batchQueue = [];
  retryQueue = [];
  retryCount = 0;
  perfSampleRate = 1;
  started = false;
  options = undefined;
}

function serverUrl(): string {
  return options?.serverUrl ?? '';
}

function beaconUrl(): string {
  return `${serverUrl()}/rum/v1/beacon`;
}

async function loadConfig(): Promise<void> {
  try {
    const response = await fetch(`${serverUrl()}/rum/v1/config?projectKey=${encodeURIComponent(options!.projectKey)}`);
    let replaySampleRate = 1;
    if (response.ok) {
      const config = (await response.json()) as RumConfig;
      perfSampleRate = clampRate(config.perfSampleRate);
      replaySampleRate = clampRate(config.replaySampleRate);
    }

    startReplayIfSampled(replaySampleRate);
  } catch {
    // 配置拉取失败保持默认全采样
    startReplayIfSampled(1);
  }
}

/** 按回放采样率决定是否启动录制（采样命中或配置缺失全采时启动）. */
function startReplayIfSampled(replaySampleRate: number): void {
  if (!started || replaySession) {
    return;
  }

  if (!options?.replayRecorderFactory || Math.random() >= replaySampleRate) {
    return;
  }

  replaySession = new ReplaySession({
    recorderFactory: options.replayRecorderFactory,
    chunkEvents: options.replayChunkEvents,
    chunkIntervalMs: options.replayChunkIntervalMs,
    sink: (chunk) =>
      track({
        type: 'replay-chunk',
        seq: chunk.seq,
        blobBase64: chunk.blobBase64,
        ts: chunk.ts,
      }),
  });
}

function clampRate(rate: number): number {
  if (!Number.isFinite(rate)) {
    return 1;
  }

  return Math.min(1, Math.max(0, rate));
}

function track(event: Partial<RumEvent>): void {
  if (!started) {
    return;
  }

  const built = buildEvent(event);
  if (!shouldSampleEvent(built)) {
    return;
  }

  // 实时通道启用时点击即发即传；失败回落批量队列（T616）
  if (built.type === 'click' && realtimeChannel) {
    const click = built as RumEvent & { extra?: Record<string, unknown> };
    void realtimeChannel
      .sendClick({
        sessionId: sessionId(),
        pagePath: built.pagePath,
        x: asNumber(click.extra?.x),
        y: asNumber(click.extra?.y),
        pageX: asNumber(click.extra?.pageX),
        pageY: asNumber(click.extra?.pageY),
        docW: asNumber(click.extra?.docW),
        docH: asNumber(click.extra?.docH),
        selector: typeof click.extra?.selector === 'string' ? click.extra.selector : undefined,
        timestamp: built.ts,
      })
      .then((sent) => {
        if (!sent) {
          enqueue(built);
        }
      });
    return;
  }

  enqueue(built);
}

function enqueue(built: RumEvent): void {
  batchQueue.push(built);
  if (batchQueue.length >= (options?.batchSize ?? DEFAULT_BATCH_SIZE)) {
    void flush();
  }
}

function asNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

/** 供测试注入同步 flush. */
export async function flush(): Promise<boolean> {
  const batch = [...retryQueue.splice(0), ...batchQueue.splice(0)];
  if (batch.length === 0) {
    return true;
  }

  // T612 缺陷修复：beacon 请求体必须携带 projectKey/sessionId（服务端空 projectKey 拒收）
  const ok = await transport(
    beaconUrl(),
    JSON.stringify({ projectKey: options!.projectKey, sessionId: sessionId(), events: batch }),
  );
  if (!ok) {
    retryQueue = retryQueue.concat(batch);
    retryCount += 1;
    if (retryCount > (options?.maxRetries ?? DEFAULT_MAX_RETRIES)) {
      retryQueue = [];
      retryCount = 0;
    }

    return false;
  }

  retryCount = 0;
  return true;
}

function scheduleFlush(): void {
  flushTimer = setTimeout(() => {
    void flush().finally(() => {
      if (started) {
        scheduleFlush();
      }
    });
  }, options?.flushIntervalMs ?? DEFAULT_FLUSH_INTERVAL_MS);
}

/** 已安装埋点的还原函数（reset 卸载用，避免重复 init 叠加监听器）. */
let instrumentationDetachers: Array<() => void> = [];

function installInstrumentation(): void {
  const globalObj = typeof window !== 'undefined' ? window : undefined;
  if (!globalObj) {
    return;
  }

  instrumentationDetachers = [
    installJsError(globalObj),
    installPageview(globalObj),
    installApiTiming(globalObj),
    installVitals(globalObj),
    installClickTracking(globalObj),
  ];
}

function uninstallInstrumentation(): void {
  for (const detach of instrumentationDetachers) {
    try {
      detach();
    } catch {
      // 埋点环境可能已被宿主改动，忽略
    }
  }

  instrumentationDetachers = [];
}

function installJsError(globalObj: Window): () => void {
  const onError = (event: Event) => {
    const errorEvent = event as ErrorEvent;
    track({
      type: 'error',
      name: errorEvent.message,
      extra: {
        stack: errorEvent.error?.stack ?? undefined,
        line: errorEvent.lineno,
        col: errorEvent.colno,
        file: errorEvent.filename,
      },
    });
  };

  const onRejection = (event: Event) => {
    const rejection = event as PromiseRejectionEvent;
    const reason = rejection.reason;
    track({
      type: 'error',
      name: reason instanceof Error ? reason.message : String(reason),
      extra: {
        stack: reason instanceof Error ? reason.stack : undefined,
        kind: 'unhandledrejection',
      },
    });
  };

  globalObj.addEventListener('error', onError);
  globalObj.addEventListener('unhandledrejection', onRejection);
  return () => {
    globalObj.removeEventListener('error', onError);
    globalObj.removeEventListener('unhandledrejection', onRejection);
  };
}

function installPageview(globalObj: Window): () => void {
  const sendPv = () => track({ type: 'pageview', extra: { ua: globalObj.navigator.userAgent, domain: globalObj.location.hostname } });

  const history = globalObj.history;
  const originalPush = history.pushState.bind(history);
  const originalReplace = history.replaceState.bind(history);
  history.pushState = function pushState(this: History, ...args: Parameters<History['pushState']>) {
    originalPush(...args);
    sendPv();
  };
  history.replaceState = function replaceState(this: History, ...args: Parameters<History['replaceState']>) {
    originalReplace(...args);
    sendPv();
  };
  globalObj.addEventListener('popstate', sendPv);
  sendPv();
  return () => {
    history.pushState = originalPush;
    history.replaceState = originalReplace;
    globalObj.removeEventListener('popstate', sendPv);
  };
}

interface TrackedXhr extends XMLHttpRequest {
  __sdataRumStart?: number;
  __sdataRumMethod?: string;
  __sdataRumUrl?: string;
}

function installApiTiming(globalObj: Window): () => void {
  const nativeFetch = globalObj.fetch;
  const xhrProto = XMLHttpRequest.prototype as TrackedXhr & {
    open: XMLHttpRequest['open'];
    send: XMLHttpRequest['send'];
  };
  const originalOpen = xhrProto.open;
  const originalSend = xhrProto.send;

  if (nativeFetch) {
    globalObj.fetch = async function patchedFetch(input: RequestInfo | URL, initRequest?: RequestInit): Promise<Response> {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      const method = initRequest?.method ?? 'GET';
      const startedAt = Date.now();
      try {
        const response = await nativeFetch.call(globalObj, input, initRequest);
        trackApi(method, url, Date.now() - startedAt, response.status);
        return response;
      } catch (error) {
        trackApi(method, url, Date.now() - startedAt, 0);
        throw error;
      }
    };
  }

  xhrProto.open = function open(this: TrackedXhr, ...args: unknown[]) {
    const [method, url] = args as [string, string | URL, ...unknown[]];
    this.__sdataRumMethod = method;
    this.__sdataRumUrl = String(url);
    return (originalOpen as (...openArgs: unknown[]) => void).apply(this, args);
  } as XMLHttpRequest['open'];

  xhrProto.send = function send(this: TrackedXhr, body?: Document | XMLHttpRequestBodyInit | null) {
    this.__sdataRumStart = Date.now();
    this.addEventListener('loadend', () => {
      trackApi(this.__sdataRumMethod ?? 'GET', this.__sdataRumUrl ?? '', Date.now() - (this.__sdataRumStart ?? Date.now()), this.status);
    });
    return originalSend.call(this, body);
  } as XMLHttpRequest['send'];

  return () => {
    if (nativeFetch) {
      globalObj.fetch = nativeFetch;
    }

    xhrProto.open = originalOpen;
    xhrProto.send = originalSend;
  };

  function trackApi(method: string, url: string, durationMs: number, status: number): void {
    // 探针自身上报（beacon/config）不计 api 事件，避免自反馈（T616）
    if (url.includes('/rum/v1/')) {
      return;
    }

    track({
      type: 'custom',
      name: 'api',
      extra: {
        method,
        url,
        durationMs,
        status,
      },
    });
  }
}

/** 点击坐标采集（T615/T616）：capture 阶段监听；视口坐标(x/y) + 文档绝对坐标(pageX/pageY，滚动页面不位移) + 目标摘要. */
function installClickTracking(globalObj: Window): () => void {
  const onClick = (event: Event) => {
    const mouse = event as MouseEvent;
    const target = mouse.target as HTMLElement | null;
    const docElement = globalObj.document?.documentElement;
    const scrollX = globalObj.scrollX ?? docElement?.scrollLeft ?? 0;
    const scrollY = globalObj.scrollY ?? docElement?.scrollTop ?? 0;
    track({
      type: 'click',
      name: target?.tagName?.toLowerCase() || undefined,
      extra: {
        x: mouse.clientX,
        y: mouse.clientY,
        pageX: mouse.clientX + scrollX,
        pageY: mouse.clientY + scrollY,
        pageW: globalObj.innerWidth,
        pageH: globalObj.innerHeight,
        docW: docElement?.scrollWidth,
        docH: docElement?.scrollHeight,
        selector: describeTarget(target),
        targetId: target?.id || undefined,
      },
    });
  };

  globalObj.addEventListener('click', onClick, true);
  return () => {
    globalObj.removeEventListener('click', onClick, true);
  };
}

/** 目标摘要（父>子 两级 tag#id.class 短链，截断 128 字符；服务端聚合点击目标 TopN 用）. */
function describeTarget(target: Element | null, depth = 2): string | undefined {
  if (!target || depth <= 0) {
    return undefined;
  }

  const tag = target.tagName?.toLowerCase();
  if (!tag) {
    return undefined;
  }

  let desc = tag;
  if (target.id) {
    desc += `#${target.id}`;
  } else if (typeof target.className === 'string' && target.className.trim()) {
    const classes = target.className.trim().split(/\s+/).slice(0, 2).join('.');
    if (classes) {
      desc += `.${classes}`;
    }
  }

  const parent = describeTarget(target.parentElement, depth - 1);
  const full = parent ? `${parent}>${desc}` : desc;
  return full.slice(0, 128);
}

function installVitals(globalObj: Window): () => void {
  const observerCtor = (globalObj as Window & { PerformanceObserver?: typeof PerformanceObserver }).PerformanceObserver;
  if (!observerCtor) {
    return () => undefined;
  }

  const report = (name: string, value: number) => track({ type: 'vital', name, value });
  const observers: PerformanceObserver[] = [];

  try {
    const lcp = new observerCtor((list) => {
      const entries = list.getEntries();
      const last = entries[entries.length - 1];
      if (last) {
        report('LCP', last.startTime);
      }
    });
    lcp.observe({ type: 'largest-contentful-paint', buffered: true } as PerformanceObserverInit);
    observers.push(lcp);
  } catch {
    // 浏览器不支持 LCP 观测
  }

  try {
    const paint = new observerCtor((list) => {
      for (const entry of list.getEntries()) {
        if (entry.name === 'first-contentful-paint') {
          report('FCP', entry.startTime);
        }
      }
    });
    paint.observe({ type: 'paint', buffered: true } as PerformanceObserverInit);
    observers.push(paint);
  } catch {
    // 浏览器不支持 paint 观测
  }

  return () => {
    for (const observer of observers) {
      observer.disconnect();
    }
  };
}
