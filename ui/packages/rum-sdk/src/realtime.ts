/**
 * RUM SDK 实时通道（T616）：SignalR 直连 /hub/rum?projectKey=，点击即发即传；
 * 连接失败/断线返回 false，由调用方回落批量 beacon 通道（不丢数据）.
 */

/** 连接抽象（测试可注入）. */
export interface RealtimeConnection {
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  stop(): Promise<void>;
}

/** 连接工厂（测试/宿主可注入自定义传输）. */
export type ConnectionFactory = (url: string) => Promise<RealtimeConnection>;

/** 实时点击载荷（与服务端 RumLiveClickEvent camelCase 对齐）. */
export interface RealtimeClickPayload {
  sessionId: string;
  pagePath?: string;
  x?: number;
  y?: number;
  pageX?: number;
  pageY?: number;
  docW?: number;
  docH?: number;
  selector?: string;
  timestamp?: string;
}

/** 实时通道句柄. */
export interface RealtimeChannel {
  /** 尝试直报一次点击；未连接/失败返回 false（回落批量通道）. */
  sendClick(click: RealtimeClickPayload): Promise<boolean>;
  /** 停止连接（卸载用）. */
  stop(): Promise<void>;
}

/** 自动重连延迟序列（ms）. */
const RECONNECT_DELAYS = [0, 2000, 5000, 10000, 30000];

/** 默认工厂：@microsoft/signalr 动态加载（未启用实时时不加载，零体积开销）. */
export async function signalRConnectionFactory(url: string): Promise<RealtimeConnection> {
  const signalR = await import('@microsoft/signalr');
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(url, { transport: signalR.HttpTransportType.WebSockets })
    .withAutomaticReconnect(RECONNECT_DELAYS)
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  await connection.start();
  return connection;
}

/**
 * 创建实时通道（懒连接：首次 sendClick 才建立连接；失败不重试直到下次点击）.
 */
export function createRealtimeChannel(options: {
  serverUrl: string;
  projectKey: string;
  factory?: ConnectionFactory;
}): RealtimeChannel {
  const url = `${options.serverUrl}/hub/rum?projectKey=${encodeURIComponent(options.projectKey)}`;
  const factory = options.factory ?? signalRConnectionFactory;
  let connection: RealtimeConnection | null = null;
  let starting: Promise<RealtimeConnection | null> | null = null;

  const ensureConnection = (): Promise<RealtimeConnection | null> => {
    if (connection) {
      return Promise.resolve(connection);
    }

    starting ??= factory(url)
      .then((conn) => {
        connection = conn;
        return conn;
      })
      .catch(() => {
        starting = null; // 下次点击重试
        return null;
      });
    return starting;
  };

  return {
    async sendClick(click: RealtimeClickPayload): Promise<boolean> {
      const conn = await ensureConnection();
      if (!conn) {
        return false;
      }

      try {
        return (await conn.invoke('SendClick', click)) !== false;
      } catch {
        return false;
      }
    },
    async stop(): Promise<void> {
      const conn = connection;
      connection = null;
      starting = null;
      if (conn) {
        try {
          await conn.stop();
        } catch {
          // 连接已断，忽略
        }
      }
    },
  };
}
