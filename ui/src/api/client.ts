import { createSearchParams } from 'react-router';
import { useAuthStore } from '@/store/auth';

/** 统一业务错误体（后端 BusinessValidationResult）. */
export interface BusinessError {
  statusCode: number;
  message: string;
  detail?: string | null;
}

export class ApiError extends Error {
  readonly status: number;
  readonly business?: BusinessError;

  constructor(status: number, business?: BusinessError, message?: string) {
    super(message ?? business?.message ?? `HTTP ${status}`);
    this.status = status;
    this.business = business;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = useAuthStore.getState().token;
  const headers = new Headers(init?.headers);
  if (!headers.has('Content-Type') && init?.body) {
    headers.set('Content-Type', 'application/json');
  }
  if (token) {
    headers.set('Authorization', `Bearer ${token}`);
  }

  const response = await fetch(`/api${path}`, { ...init, headers });
  if (!response.ok) {
    let business: BusinessError | undefined;
    try {
      const body = await response.json();
      if (body && typeof body.message === 'string') {
        business = body as BusinessError;
      }
    } catch {
      // 非 JSON 错误体
    }

    // 401：token 失效，清除登录态
    if (response.status === 401 && token) {
      useAuthStore.getState().clear();
    }
    throw new ApiError(response.status, business);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

/** GET. */
export function get<T>(path: string, params?: object, signal?: AbortSignal): Promise<T> {
  const query = params ? createSearchParams(cleanParams(params as Record<string, unknown>)).toString() : '';
  return request<T>(query ? `${path}?${query}` : path, { signal });
}

/** POST. */
export function post<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  return request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body), signal });
}

/** PUT. */
export function put<T>(path: string, body?: unknown): Promise<T> {
  return request<T>(path, { method: 'PUT', body: body === undefined ? undefined : JSON.stringify(body) });
}

/** DELETE. */
export function del<T>(path: string): Promise<T> {
  return request<T>(path, { method: 'DELETE' });
}

function cleanParams(params: Record<string, unknown>): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') {
      continue;
    }
    if (value instanceof Date) {
      result[key] = value.toISOString();
      continue;
    }
    if (typeof value === 'object') {
      result[key] = JSON.stringify(value);
      continue;
    }
    result[key] = String(value);
  }
  return result;
}
