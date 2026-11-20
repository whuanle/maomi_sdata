import { describe, expect, it } from 'vitest';
import { groupDropTarget } from '@/design-system/dashboardGroupDrop';

const group = { i: 'g', x: 0, y: 0, w: 12, h: 4 };
describe('group drop target', () => {
  it('accepts the center but keeps the edge available for sorting', () => {
    expect(groupDropTarget([group], { i: 'p', x: 3, y: 1, w: 6, h: 2 }, ['g'])).toBe('g');
    expect(groupDropTarget([group], { i: 'p', x: 0, y: 0, w: 2, h: 2 }, ['g'])).toBeUndefined();
  });
  it('does not nest groups or accept unrelated canvas items', () => {
    expect(groupDropTarget([group], { ...group, i: 'other' }, ['g', 'other'])).toBeUndefined();
    expect(groupDropTarget([group], { ...group, i: 'p' }, [])).toBeUndefined();
  });
});
