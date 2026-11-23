import { describe, expect, it, vi } from 'vitest';
import { saveRetentionSettings } from '../settings';
import { put } from '../client';
import type { RetentionSettings } from '../types';

vi.mock('../client', () => ({ get: vi.fn(), post: vi.fn(), put: vi.fn().mockResolvedValue({}) }));

describe('retention request contract', () => {
  it('wraps the retention plan in the backend command', async () => {
    const tier = { hotDays: 1, coldDays: 90 };
    const plan: RetentionSettings = { logs: tier, traces: tier, metrics: tier, iot: { hotDays: 365, coldDays: 0 }, rum: tier };
    await saveRetentionSettings(plan);
    expect(put).toHaveBeenCalledWith('/settings/retention', { retention: plan });
  });
});
