import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchMqConsumption } from '../monitor';
import { ApiError, get } from '../client';

vi.mock('../client', async (original) => ({ ...await original<typeof import('../client')>(), get: vi.fn() }));
afterEach(() => { vi.useRealTimers(); vi.resetAllMocks(); });

describe('MQ status diagnostics', () => {
  it('rejects invalid response contracts', async () => {
    vi.mocked(get).mockResolvedValueOnce({ instances: null });
    await expect(fetchMqConsumption()).rejects.toMatchObject({ code: 'contract' });
  });
  it('distinguishes HTTP and JSON errors', async () => {
    vi.mocked(get).mockRejectedValueOnce(new ApiError(403));
    await expect(fetchMqConsumption()).rejects.toMatchObject({ code: 'request', status: 403 });
    vi.mocked(get).mockRejectedValueOnce(new SyntaxError('private response'));
    await expect(fetchMqConsumption()).rejects.toMatchObject({ code: 'response', message: 'response' });
  });
  it('aborts requests after eight seconds', async () => {
    vi.useFakeTimers();
    vi.mocked(get).mockImplementationOnce((_path, _params, signal) => new Promise((_resolve, reject) => {
      signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }));
    const check = expect(fetchMqConsumption()).rejects.toMatchObject({ code: 'timeout' });
    await vi.advanceTimersByTimeAsync(8000);
    await check;
  });
});
