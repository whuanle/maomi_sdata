import { describe, expect, it } from 'vitest';
import type { DashboardConfig, PanelConfig } from '@/api/types';
import { applyGroup, clonePanel, dissolveGroup, findPanel, leafPanels, movePanelIntoGroup, removePanel, replacePanel } from '../dashboardGroups';
import { normalizeDashboard } from '../dashboardLayout';

const a: PanelConfig = { id: 'a', title: 'A', type: 'markdown', markdown: 'alpha', layout: { x: 0, y: 0, w: 6, h: 3 } };
const b: PanelConfig = { ...a, id: 'b', title: 'B', markdown: 'beta', layout: { x: 6, y: 0, w: 6, h: 3 } };
const config: DashboardConfig = { version: 2, refreshSeconds: 0, panels: [a, b] };
const group: PanelConfig = { id: 'g', title: 'Group', type: 'group', panels: [a, b], defaultPanelId: 'b' };

describe('dashboard groups', () => {
  it('groups existing panels and round trips the v3 contract without losing definitions', () => {
    const next = applyGroup(config, group);
    expect(next.version).toBe(3);
    expect(next.panels).toHaveLength(1);
    expect(leafPanels(next.panels)).toEqual([a, b]);
    expect(normalizeDashboard(JSON.stringify(next))).toEqual({ ...next, time: { preset: '1h' } });
  });
  it('edits members, removes active tabs and dissolves without losing components', () => {
    let next = applyGroup(config, group);
    next = replacePanel(next, { ...b, title: 'Edited' });
    expect(findPanel(next, 'b')?.title).toBe('Edited');
    next = removePanel(next, 'b');
    expect(next.panels[0].defaultPanelId).toBe('a');
    expect(dissolveGroup(next, next.panels[0]).panels.map(panel => panel.id)).toEqual(['a']);
    expect(removePanel(next, 'a').panels[0].panels).toEqual([]);
  });
  it('moves deselected members out and preserves an empty container', () => {
    const next = applyGroup(config, group);
    const changed = applyGroup(next, { ...next.panels[0], panels: [a], defaultPanelId: 'a' });
    expect(changed.panels.map(panel => panel.id)).toEqual(['g', 'b']);
    expect(applyGroup(next, { ...group, panels: [] }).panels.map(panel => panel.id)).toEqual(['g', 'a', 'b']);
  });
  it('creates an empty group and moves a component inside exactly once', () => {
    const empty = applyGroup(config, { id: 'g', title: 'Group', type: 'group', panels: [] });
    expect(normalizeDashboard(JSON.stringify(empty)).panels.find(panel => panel.id === 'g')?.panels).toEqual([]);
    const next = movePanelIntoGroup(empty, 'a', 'g');
    expect(next.panels.map(panel => panel.id)).toEqual(['b', 'g']);
    expect(findPanel(next, 'g')?.panels).toEqual([a]);
    expect(movePanelIntoGroup(next, 'a', 'g')).toBe(next);
    expect(movePanelIntoGroup(next, 'g', 'g')).toBe(next);
  });
  it('copies all IDs and maps the default tab to the cloned member', () => {
    const copy = clonePanel(group, 'copy');
    expect(new Set([copy.id, ...copy.panels!.map(panel => panel.id)]).size).toBe(3);
    expect(copy.panels!.some(panel => ['a', 'b', 'g'].includes(panel.id))).toBe(false);
    expect(copy.defaultPanelId).toBe(copy.panels![1].id);
    expect(group.defaultPanelId).toBe('b');
  });
  it('rejects nested groups, duplicate IDs, missing default tabs and oversized groups', () => {
    for (const bad of [
      { ...group, panels: [a, a] }, { ...group, panels: [group] },
      { ...group, defaultPanelId: 'missing' },
      { ...group, panels: Array.from({ length: 100 }, (_, i) => ({ ...a, id: String(i) })) },
    ]) expect(() => normalizeDashboard(JSON.stringify({ ...config, version: 3, panels: [bad] }))).toThrow();
  });
});
