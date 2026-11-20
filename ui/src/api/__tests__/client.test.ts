import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { get } from '../client';
import { useAuthStore } from '@/store/auth';

beforeEach(() => {
  useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
});
afterEach(() => vi.unstubAllGlobals());

describe('API response contracts', () => {
  it('propagates the actual HTML response parsing error', async () => {
    const response = new Response('<!DOCTYPE html><html>app</html>', { headers: { 'Content-Type': 'text/html' } });
    const expected = await response.clone().json().catch(reason => reason);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response));
    await expect(get('/settings/mqtt-auth')).rejects.toThrow(expected.message);
    expect(useAuthStore.getState().token).toBe('test-token');
  });

  it('propagates malformed JSON errors without replacing them', async () => {
    const response = new Response('{broken', { headers: { 'Content-Type': 'application/json' } });
    const expected = await response.clone().json().catch(reason => reason);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response));
    await expect(get('/settings/mqtt-auth')).rejects.toThrow(expected.message);
  });

  it('keeps valid JSON and empty responses working', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(new Response('{"mode":"none","hasKey":false}', { headers: { 'Content-Type': 'application/json; charset=utf-8' } }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetch);
    await expect(get('/settings/mqtt-auth')).resolves.toEqual({ mode: 'none', hasKey: false });
    await expect(get('/empty')).resolves.toBeUndefined();
  });

  it('preserves business errors and clears expired authentication', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"statusCode":401,"message":"请重新登录"}', { status: 401, headers: { 'Content-Type': 'application/json' } })));
    await expect(get('/settings/mqtt-auth')).rejects.toThrow('请重新登录');
    expect(useAuthStore.getState().token).toBeNull();
  });
});
