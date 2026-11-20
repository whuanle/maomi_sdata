import { describe, expect, it, vi } from 'vitest';
import { saveRetentionSettings, saveDuckDbSettings } from '../settings';
import { put } from '../client';
import type { RetentionSettings } from '../types';

vi.mock('../client', () => ({ get: vi.fn(), post: vi.fn(), put: vi.fn().mockResolvedValue({}) }));

describe('retention request contract', () => {
  it('saves only the editable DuckDB memory limit', async () => {
    await saveDuckDbSettings({ memoryLimit: '2GB' });
    expect(put).toHaveBeenCalledWith('/settings/duckdb', { memoryLimit: '2GB' });
  });
  it('wraps the retention plan in the backend command', async () => {
    const tier = { hotDays: 1, coldDays: 90 };
    const plan: RetentionSettings = { logs: tier, traces: tier, metrics: tier, iot: { hotDays: 365, coldDays: 0 }, rum: tier };
    await saveRetentionSettings(plan);
    expect(put).toHaveBeenCalledWith('/settings/retention', { retention: plan });
  });
});
