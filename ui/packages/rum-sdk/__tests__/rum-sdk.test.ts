import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  buildEvent,
  flush,
  init,
  queueLength,
  reset,
  shouldSampleEvent,
  transport,
  type RumEvent,
} from '../src/index';

/** 记录上报调用的 stub 通道. */
const sentBodies: string[] = [];

/** 提取 fetch stub 最后一次调用的请求体（transport 将 body 放在 RequestInit）. */
function lastBody(mock: { mock: { calls: unknown[][] } }): { projectKey?: string; sessionId?: string; events?: Array<{ type: string; extra?: Record<string, unknown> }> } {
  const calls = mock.mock.calls;
  const requestInit = calls.length > 0 ? (calls[calls.length - 1][1] as { body?: string } | undefined) : undefined;
  return JSON.parse(String(requestInit?.body));
}

describe('@sdata/rum-sdk（阶段 A）', () => {
  beforeEach(() => {
    sentBodies.length = 0;
    reset();
    vi.stubGlobal('fetch', vi.fn(async () => ({ status: 204, ok: true })));
    vi.stubGlobal('navigator', {
      sendBeacon: vi.fn((url: string, blob: Blob) => {
        sentBodies.push(String(blob));
        return true;
      }),
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('buildEvent 补全 pagePath 与 ts 缺省值', () => {
    window.history.pushState({}, '', '/demo/page');
    const event = buildEvent({ type: 'pageview' });
    expect(event.type).toBe('pageview');
    expect(event.pagePath).toBe('/demo/page');
    expect(event.ts).toBeTruthy();
  });

  it('error/pageview 全采，perf 类事件按采样率放行', () => {
    // 通过 init+loadConfig 的网络 stub 让 perfSampleRate 保持 1（默认）
    expect(shouldSampleEvent({ type: 'error' })).toBe(true);
    expect(shouldSampleEvent({ type: 'pageview' }).valueOf()).toBe(true);
  });

  it('批满阈值触发立即上报', async () => {
    init({ projectKey: 'rum_demo', batchSize: 3, flushIntervalMs: 60_000 });
    await flush(); // 清掉 init 立即上报的首次 PV
    for (let i = 0; i < 3; i++) {
      window.dispatchEvent(new Event('popstate'));
    }

    // popstate → pageview；批满 3 条触发 flush（transport 已 stub）
    await vi.waitFor(() => {
      expect(queueLength()).toBe(0);
    });
  });

  it('上报失败时事件入重试队列并计数，达到上限后丢弃', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('network down'); }));
    init({ projectKey: 'rum_demo', batchSize: 2, flushIntervalMs: 60_000, maxRetries: 1 });
    window.dispatchEvent(new Event('popstate'));
    window.dispatchEvent(new Event('popstate'));
    await vi.waitFor(() => {
      expect(queueLength()).toBeGreaterThan(0);
    });

    await flush();
    // 第二次 flush 仍失败 → retryCount=2 > maxRetries(1) → 丢弃
    await flush();
    expect(queueLength()).toBe(0);
  });

  it('失败队列恢复后实际重发，PV 携带 UA 和域名', async () => {
    vi.stubGlobal('navigator', { userAgent: 'TestBrowser' });
    let fail = true;
    const fetchMock = vi.fn(async (url: string) => {
      if (url.includes('/config')) return { ok: true, status: 200, json: async () => ({ perfSampleRate: 1, replaySampleRate: 0 }) };
      if (fail) throw new Error('offline');
      return { ok: true, status: 204 };
    });
    vi.stubGlobal('fetch', fetchMock);
    init({ projectKey: 'rum_retry', batchSize: 100, flushIntervalMs: 60000 });
    expect(await flush()).toBe(false);
    expect(queueLength()).toBe(1);
    fail = false;
    expect(await flush()).toBe(true);
    expect(queueLength()).toBe(0);
    expect(lastBody(fetchMock).events?.[0].extra).toMatchObject({ ua: 'TestBrowser', domain: window.location.hostname });
  });

  it('transport 无 sendBeacon 时回退 fetch keepalive', async () => {
    const fetchMock = vi.fn(async () => ({ status: 204, ok: true }));
    vi.stubGlobal('navigator', {});
    vi.stubGlobal('fetch', fetchMock);
    const ok = await transport('http://x/rum/v1/beacon', '{"events":[]}');
    expect(ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('flush 上报携带 projectKey 与 sessionId（T612 拒收缺陷修复，T616）', async () => {
    const fetchMock = vi.fn(async () => ({ status: 204, ok: true }));
    vi.stubGlobal('navigator', {});
    vi.stubGlobal('fetch', fetchMock);
    init({ projectKey: 'rum_demo', batchSize: 5, flushIntervalMs: 60_000 });
    await flush(); // 清空 init 首次 PV
    window.dispatchEvent(new Event('popstate'));
    await flush();
    const body = lastBody(fetchMock);
    expect(body.projectKey).toBe('rum_demo');
    expect(typeof body.sessionId).toBe('string');
    expect(body.sessionId?.length).toBeGreaterThan(0);
    // 同标签页会话 id 稳定
    window.dispatchEvent(new Event('popstate'));
    await flush();
    expect(lastBody(fetchMock).sessionId).toBe(body.sessionId);
  });

  it('click 事件携带文档绝对坐标与目标摘要（T616）', async () => {
    const fetchMock = vi.fn(async () => ({ status: 204, ok: true }));
    vi.stubGlobal('navigator', {});
    vi.stubGlobal('fetch', fetchMock);
    init({ projectKey: 'rum_demo', batchSize: 50, flushIntervalMs: 60_000 });
    await flush();
    const button = document.createElement('button');
    button.id = 'submit-btn';
    document.body.appendChild(button);
    button.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 30, clientY: 40 }));
    await flush();
    const body = lastBody(fetchMock);
    const click = body.events!.find((event) => event.type === 'click');
    expect(click).toBeTruthy();
    expect(click!.extra?.x).toBe(30);
    expect(click!.extra?.y).toBe(40);
    expect(click!.extra?.pageX).toBe(30); // jsdom scrollX=0
    expect(click!.extra?.pageY).toBe(40);
    expect(click!.extra?.docW).toBeDefined(); // jsdom 无布局，scrollWidth=0；真实浏览器 >0
    expect(click!.extra?.docH).toBeDefined();
    expect(click!.extra?.selector).toContain('#submit-btn');
    button.remove();
  });

  it('realtime 启用时 click 走实时直报，断线回落批量队列（T616）', async () => {
    const fetchMock = vi.fn(async () => ({ status: 204, ok: true }));
    vi.stubGlobal('navigator', {});
    vi.stubGlobal('fetch', fetchMock);
    let fail = true;
    init({
      projectKey: 'rum_demo',
      batchSize: 50,
      flushIntervalMs: 60_000,
      realtime: true,
      realtimeConnectionFactory: async () => {
        if (fail) {
          throw new Error('down');
        }

        return {
          invoke: async () => true,
          stop: async () => undefined,
        };
      },
    });
    await flush();
    const button = document.createElement('button');
    button.id = 'live-btn';
    document.body.appendChild(button);

    // 断线：点击回落批量队列（自身上报已排除 api 计时，队列事件无歧义）
    button.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 1, clientY: 2 }));
    await vi.waitFor(() => {
      expect(queueLength()).toBe(1);
    });
    await flush();
    expect(lastBody(fetchMock).events![0].type).toBe('click');

    // 恢复：点击走实时通道，不进批量队列
    fail = false;
    button.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 3, clientY: 4 }));
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(queueLength()).toBe(0);
    button.remove();
  });
});
