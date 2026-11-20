import { ApiError, get, post } from './client';
import type { MqConsumptionResponse, OverviewResponse, StatusResponse } from './types';

/** 服务状态. */
export function fetchStatus() {
  return get<StatusResponse>('/monitor/status');
}

/** 实例和配置主题的消费状态. */
export class MqStatusError extends Error {
  constructor(readonly code: 'timeout' | 'request' | 'response' | 'contract', readonly status?: number) {
    super(code);
  }
}

export async function fetchMqConsumption() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 8000);
  try {
    const result = await get<MqConsumptionResponse>('/monitor/mq-consumption', undefined, controller.signal);
    if (!result || !Array.isArray(result.instances) || typeof result.sampledAt !== 'string'
      || !Number.isFinite(Date.parse(result.sampledAt)) || result.instances.some(item => !item || typeof item.instanceId !== 'number' || typeof item.state !== 'string')) {
      throw new MqStatusError('contract');
    }
    return result;
  } catch (error) {
    if (error instanceof MqStatusError) throw error;
    if (controller.signal.aborted) throw new MqStatusError('timeout');
    if (error instanceof SyntaxError) throw new MqStatusError('response');
    throw new MqStatusError('request', error instanceof ApiError ? error.status : undefined);
  } finally {
    clearTimeout(timer);
  }
}

/** 重建整个实例，不重放已确认的消息. */
export function retryMqConsumer(instanceId: number) {
  return post<boolean>('/monitor/mq-consumption/retry', { instanceId });
}

/** 存活探针. */
export function fetchHealth() {
  return get<{ status: string }>('/health');
}

/** 总览页聚合：通道健康/今日量/存储用量. */
export function fetchOverview() {
  return get<OverviewResponse>('/overview/status');
}
