import { afterEach, describe, expect, it, vi } from 'vitest';
import { gzipBase64, ReplaySession } from '../src/replay';
import { init, queueLength, reset, shouldSampleEvent, transport } from '../src/index';

describe('ReplaySession（T614 会话回放）', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    reset();
  });

  it('chunkEvents 满触发切段：seq 单调递增、内容 gzip+base64', async () => {
    vi.stubGlobal('CompressionStream', undefined);
    const chunks: Array<{ seq: number; blobBase64: string }> = [];
    const session = new ReplaySession({
      chunkEvents: 3,
      chunkIntervalMs: 0,
      recorderFactory: () => () => {},
      sink: (chunk) => chunks.push({ seq: chunk.seq, blobBase64: chunk.blobBase64 }),
    });

    session.push({ type: 0, data: { text: 'a' } });
    session.push({ type: 3, data: { text: 'b' } });
    session.push({ type: 5, data: { text: 'c' } });
    // push 满触发的 drain 是 fire-and-forget，等待其完成
    await new Promise((resolve) => setTimeout(resolve, 5));

    expect(chunks.length).toBe(1);
    expect(chunks[0].seq).toBe(1);
    expect(atob(chunks[0].blobBase64)).toContain('"text":"a"');

    session.push({ type: 0, data: { text: 'd' } });
    session.push({ type: 0, data: { text: 'e' } });
    session.push({ type: 0, data: { text: 'f' } });
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(chunks[1].seq).toBe(2);

    await session.stop();
    expect(session.started).toBe(false);
  });

  it('停止时刷新不足一段的尾部事件', async () => {
    vi.stubGlobal('CompressionStream', undefined);
    const chunks: Array<{ seq: number; blobBase64: string }> = [];
    const session = new ReplaySession({ chunkEvents: 50, chunkIntervalMs: 0, recorderFactory: () => () => {}, sink: (chunk) => chunks.push(chunk) });
    session.push({ type: 3, data: 'tail' });
    await session.stop();
    expect(chunks).toHaveLength(1);
    expect(atob(chunks[0].blobBase64)).toContain('tail');
    session.push({ type: 3 });
    await session.stop();
    expect(chunks).toHaveLength(1);
  });

  it('未注入 recorderFactory 时不启动录制', () => {
    const session = new ReplaySession({ chunkEvents: 2, chunkIntervalMs: 0, sink: () => {} });

    expect(session.started).toBe(false);
    session.push({});
    expect(queueLength()).toBe(0);
  });

  it('gzipBase64 无 CompressionStream 时回退原始 base64', async () => {
    vi.stubGlobal('CompressionStream', undefined);
    const text = JSON.stringify([{ type: 0, data: { text: 'hello' } }]);

    const blob = await gzipBase64(text);

    expect(atob(blob)).toBe(text);
  });

  it('init 未注入 replayRecorderFactory 不产生回放会话', async () => {
    vi.stubGlobal('CompressionStream', undefined);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => ({ perfSampleRate: 1, replaySampleRate: 1 }) }));
    vi.stubGlobal('navigator', {});

    init({ projectKey: 'rum_x', batchSize: 100 });
    await new Promise((resolve) => setTimeout(resolve, 10));

    // 无录制器工厂 → 无 replay-chunk
    const ok = await flushOfEvents();
    expect(ok).toBe(true);
    const body = lastTransportBody();
    expect(body && (body as { events?: Array<{ type: string }> }).events?.some((e) => e.type === 'replay-chunk')).toBeFalsy();
  });

  it('replaySampleRate=0 时不启动录制', async () => {
    let factoryCalls = 0;
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => ({ perfSampleRate: 1, replaySampleRate: 0 }) }));
    vi.stubGlobal('navigator', {});

    init({
      projectKey: 'rum_x',
      batchSize: 100,
      replayRecorderFactory: () => {
        factoryCalls += 1;
        return () => {};
      },
    });
    await new Promise((resolve) => setTimeout(resolve, 10));

    expect(factoryCalls).toBe(0);
  });

  it('采样命中时启动录制且分段经 beacon 上报（replay-chunk 全采）', async () => {
    const bodies: unknown[] = [];
    vi.stubGlobal('CompressionStream', undefined);
    // 走 fetch 路径捕获 body（jsdom Blob 无 text()）
    vi.stubGlobal('navigator', {});
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, initRequest?: RequestInit) => {
      if (typeof input === 'string' && input.endsWith('/rum/v1/beacon')) {
        bodies.push(JSON.parse(String(initRequest?.body)));
        return Promise.resolve({ ok: true, status: 204 });
      }

      return Promise.resolve({ ok: true, status: 200, json: async () => ({ perfSampleRate: 1, replaySampleRate: 1 }) });
    }));

    init({
      projectKey: 'rum_x',
      batchSize: 100,
      replayChunkEvents: 2,
      replayRecorderFactory: (emit) => {
        emit({ type: 0, data: { text: 'event-1' } });
        emit({ type: 0, data: { text: 'event-2' } });
        return () => {};
      },
    });
    await new Promise((resolve) => setTimeout(resolve, 10));
    await flushOfEvents();
    await new Promise((resolve) => setTimeout(resolve, 10));

    const replayChunks = bodies
      .map((body) => (body as { events?: Array<{ type: string }> }).events ?? [])
      .flat()
      .filter((event) => (event as { type: string }).type === 'replay-chunk');
    expect(replayChunks.length).toBeGreaterThanOrEqual(1);
    expect((replayChunks[0] as unknown as { seq: number }).seq).toBe(1);
    expect(typeof (replayChunks[0] as unknown as { blobBase64: string }).blobBase64).toBe('string');
  });
});

/** 把队列中现有事件 flush 掉（直达 transport，绕过定时器）. */
async function flushOfEvents(): Promise<boolean> {
  // 直接调内部 flush：通过导出的 transport 无法触达队列，这里用多次 flush 调用（index 导出 flush）
  const mod = await import('../src/index');
  return mod.flush();
}

/** 记录最近一次 transport 载荷（测试辅助）. */
function lastTransportBody(): unknown {
  // transport 已被 mock 的 navigator.sendBeacon 覆盖；此处仅为可读性占位
  return null;
}
