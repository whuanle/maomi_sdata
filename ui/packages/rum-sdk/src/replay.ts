/**
 * 会话回放录制（T614，C3 replay-chunk 协议）：
 * 可插拔录制器（宿主应用注入 rrweb record，SDK 本体保持零依赖）→ 事件缓冲
 * → CompressionStream('gzip')（不可用时回退原始 base64）→ base64
 * → type=replay-chunk 分段上报（seq 单调递增）.
 */

/** rrweb record 兼容的录制器最小接口. */
export interface ReplayRecorder {
  /** 停止录制并返回剩余事件. */
  (): void;
}

/** 录制器工厂：返回 start/stop 语义（宿主接入 rrweb 时传 `() => rrweb.record({...})`）. */
export type ReplayRecorderFactory = (emit: (event: unknown) => void) => ReplayRecorder;

/** 回放上报通道：分段构造后由调用方送入 beacon 队列. */
export type ReplayChunkSink = (chunk: { seq: number; blobBase64: string; ts: string }) => void;

/** 回放模块配置. */
export interface ReplayOptions {
  /** 录制器工厂（不可用时不启动录制）. */
  recorderFactory?: ReplayRecorderFactory;
  /** 分段事件条数（默认 50）. */
  chunkEvents?: number;
  /** 分段定时 ms（默认 5000；与 chunkEvents 任一满足即切段）. */
  chunkIntervalMs?: number;
  /** 每 tick 消费的分段出口. */
  sink: ReplayChunkSink;
}

/** gzip 压缩：优先 CompressionStream('gzip')，不可用回退原始 UTF-8 字节. */
export async function gzipBase64(text: string): Promise<string> {
  const bytes = new TextEncoder().encode(text);
  const compression = typeof CompressionStream !== 'undefined' ? new CompressionStream('gzip') : undefined;
  if (!compression) {
    return base64Of(bytes);
  }

  const stream = new Blob([bytes]).stream().pipeThrough(compression);
  const compressed = new Uint8Array(await new Response(stream).arrayBuffer());
  return base64Of(compressed);
}

/** base64（分块避免 call stack 限制）. */
export function base64Of(bytes: Uint8Array): string {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }

  return btoa(binary);
}

/**
 * 回放会话管理：启动录制、缓冲事件、按条数/时间切段并上报.
 */
export class ReplaySession {
  private readonly options: Required<Pick<ReplayOptions, 'chunkEvents' | 'chunkIntervalMs'>>;
  private readonly sink: ReplayChunkSink;
  private readonly buffer: unknown[] = [];
  private readonly timer: ReturnType<typeof setInterval> | undefined;
  private recorder: ReplayRecorder | undefined;
  private stopped = false;
  private seq = 0;
  private draining: Promise<void> | undefined;

  constructor(options: ReplayOptions) {
    this.options = {
      chunkEvents: options.chunkEvents ?? 50,
      chunkIntervalMs: options.chunkIntervalMs ?? 5000,
    };
    this.sink = options.sink;
    if (options.recorderFactory) {
      // 注意：factory 可能在返回前（构造期间）同步 emit，先置占位再赋值，保证 emit→push 有效
      this.recorder = () => {};
      this.recorder = options.recorderFactory((event) => this.push(event));
    }

    if (this.options.chunkIntervalMs > 0) {
      this.timer = setInterval(() => void this.drain(), this.options.chunkIntervalMs);
    }
  }

  /** 录制是否已启动（stop 后为 false）. */
  get started(): boolean {
    return !this.stopped && this.recorder !== undefined;
  }

  /** 手动投递事件（测试/外部采集）. */
  push(event: unknown): void {
    if (this.stopped) {
      return;
    }

    this.buffer.push(event);
    if (this.buffer.length >= this.options.chunkEvents) {
      void this.drain();
    }
  }

  /** 缓冲切段上报（缓冲空则跳过）. */
  async drain(): Promise<void> {
    if (this.draining) {
      await this.draining;
    }
    if (this.buffer.length === 0) return;
    const chunk = this.buffer.splice(0);
    const seq = ++this.seq;
    this.draining = (async () => {
      const blobBase64 = await gzipBase64(JSON.stringify(chunk));
      this.sink({ seq, blobBase64, ts: new Date().toISOString() });
    })();
    try {
      await this.draining;
    } finally {
      this.draining = undefined;
    }
  }

  /** 停止录制并上报剩余事件. */
  async stop(): Promise<void> {
    if (this.stopped) {
      return;
    }

    this.stopped = true;
    if (this.timer) {
      clearInterval(this.timer);
    }

    this.recorder?.();
    this.recorder = undefined;
    await this.drain();
  }
}
