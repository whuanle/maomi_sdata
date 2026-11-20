import { describe, expect, it } from 'vitest';
import { appendPanel, emptyDashboard, normalizeDashboard } from '../dashboardLayout';

describe('Dashboard layout migration', () => {
  it('preserves valid small v2 panels rather than expanding them on load', () => {
    const config = { version: 2, panels: [{ id: 'small', type: 'stat', layout: { x: 11, y: 0, w: 1, h: 1 } }] };
    expect(normalizeDashboard(JSON.stringify(config)).panels[0].layout).toEqual(config.panels[0].layout);
  });
  it('assigns deterministic non-overlapping coordinates and preserves screen fields', () => {
    const config = normalizeDashboard(JSON.stringify({ panels: [
      { id: 'a', type: 'line', layout: { w: 6, h: 4 } },
      { id: 'b', type: 'line', layout: { w: 6, h: 4 } },
      { id: 'c', type: 'stat', layout: { w: 12, h: 2 } },
    ], screen: { refreshInterval: 15 }, extension: { enabled: true } }));
    expect(config.version).toBe(2);
    expect(config.panels.map(p => p.layout)).toEqual([
      { x: 0, y: 0, w: 6, h: 4 }, { x: 6, y: 0, w: 6, h: 4 }, { x: 0, y: 4, w: 12, h: 2 },
    ]);
    expect(config.screen).toEqual({ refreshInterval: 15 });
    expect(config.extension).toEqual({ enabled: true });
    expect(normalizeDashboard(JSON.stringify(config))).toEqual(config);
  });
  it('rejects malformed, unsupported and duplicate configurations', () => {
    expect(() => normalizeDashboard('{')).toThrow();
    expect(() => normalizeDashboard('{"version":99}')).toThrow();
    expect(() => normalizeDashboard('{"panels":[{"id":"a"},{"id":"a"}]}')).toThrow();
  });
  it('appends components below the existing canvas', () => {
    const first = appendPanel(emptyDashboard(), { id: 'a', title: 'A', type: 'stat' });
    const second = appendPanel(first, { id: 'b', title: 'B', type: 'line' });
    expect(second.panels[1].layout).toMatchObject({ x: 0, y: 4 });
  });
});
