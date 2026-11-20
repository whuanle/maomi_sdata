import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createRealtimeChannel, type ConnectionFactory, type RealtimeConnection } from '../src/realtime';

/** 构造可编程连接 stub. */
function makeConnection(invokeError?: Error): RealtimeConnection & { invokes: Array<{ method: string; args: unknown[] }> } {
  const invokes: Array<{ method: string; args: unknown[] }> = [];
  return {
    invokes,
    async invoke(method: string, ...args: unknown[]) {
      invokes.push({ method, args });
      if (invokeError) {
        throw invokeError;
      }

      return true;
    },
    async stop() {
      return;
    },
  };
}

describe('@sdata/rum-sdk realtime 通道（T616）', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('连接成功时点击即发即传，载荷对齐服务端 camelCase', async () => {
    const connection = makeConnection();
    const factory = vi.fn<ConnectionFactory>(async () => connection);
    const channel = createRealtimeChannel({ serverUrl: '', projectKey: 'rum_demo', factory });

    const sent = await channel.sendClick({ sessionId: 'sid1', pagePath: '/a', pageX: 12, pageY: 34 });
    expect(sent).toBe(true);
    expect(factory).toHaveBeenCalledWith('/hub/rum?projectKey=rum_demo');
    expect(connection.invokes).toHaveLength(1);
    expect(connection.invokes[0].method).toBe('SendClick');
    expect(connection.invokes[0].args[0]).toMatchObject({ sessionId: 'sid1', pagePath: '/a', pageX: 12, pageY: 34 });
    await channel.stop();
  });

  it('连接失败返回 false，由调用方回落批量通道；下次点击重试连接', async () => {
    let fail = true;
    const factory = vi.fn<ConnectionFactory>(async () => {
      if (fail) {
        throw new Error('server down');
      }

      return makeConnection();
    });
    const channel = createRealtimeChannel({ serverUrl: '', projectKey: 'rum_demo', factory });

    expect(await channel.sendClick({ sessionId: 'sid1' })).toBe(false);
    expect(await channel.sendClick({ sessionId: 'sid1' })).toBe(false); // 失败状态不缓存连接

    fail = false;
    expect(await channel.sendClick({ sessionId: 'sid1' })).toBe(true); // 恢复后重连成功
    expect(factory).toHaveBeenCalledTimes(3);
    await channel.stop();
  });

  it('invoke 抛错（缓冲满/服务端拒绝）返回 false', async () => {
    const connection = makeConnection(new Error('hub rejected'));
    const channel = createRealtimeChannel({ serverUrl: '', projectKey: 'rum_demo', factory: async () => connection });
    expect(await channel.sendClick({ sessionId: 'sid1' })).toBe(false);
    await channel.stop();
  });

  it('服务端返回 false 时明确回落', async () => {
    const channel = createRealtimeChannel({ serverUrl: '', projectKey: 'rum_demo', factory: async () => ({ invoke: async () => false, stop: async () => {} }) });
    expect(await channel.sendClick({ sessionId: 's' })).toBe(false);
    await channel.stop();
  });

  it('stop 后连接置空，后续点击重新建连', async () => {
    const connection = makeConnection();
    const factory = vi.fn<ConnectionFactory>(async () => connection);
    const channel = createRealtimeChannel({ serverUrl: '', projectKey: 'rum_demo', factory });
    await channel.sendClick({ sessionId: 'sid1' });
    await channel.stop();
    await channel.sendClick({ sessionId: 'sid1' });
    expect(factory).toHaveBeenCalledTimes(2);
    await channel.stop();
  });
});
