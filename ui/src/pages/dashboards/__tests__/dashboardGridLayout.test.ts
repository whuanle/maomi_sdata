import { describe, expect, it } from 'vitest';
import { insertGridItem, intersects, settleGridItem, type GridItem } from '@/design-system/dashboardGridLayout';

const items: GridItem[] = [
  { i: 'a', x: 0, y: 0, w: 6, h: 2 }, { i: 'b', x: 6, y: 0, w: 6, h: 4 },
  { i: 'c', x: 0, y: 4, w: 12, h: 3 }, { i: 'far', x: 0, y: 40, w: 12, h: 2 },
];
function valid(result: GridItem[]) {
  for (const item of result) {
    expect(item.w).toBe(items.find(value => value.i === item.i)!.w);
    expect(item.h).toBe(items.find(value => value.i === item.i)!.h);
    expect(result.filter(other => other.i !== item.i).some(other => intersects(item, other))).toBe(false);
  }
  expect(result.find(item => item.i === 'far')).toEqual(items[3]);
}
describe('deterministic grid transactions', () => {
  it('inserts before and after without exchanging dimensions or moving distant panels', () => {
    const before = insertGridItem(items, 'c', 'a');
    expect(before.map(item => item.i)).toEqual(['c', 'a', 'b', 'far']);
    valid(before);
    const after = insertGridItem(items, 'a', 'c', true);
    expect(after.map(item => item.i)).toEqual(['b', 'c', 'a', 'far']);
    valid(after);
    expect(items[0].y).toBe(0);
  });
  it('commits only the final overlap and has no cumulative collision drift', () => {
    const result = settleGridItem(items, { ...items[2], x: 0, y: 0 });
    valid(result);
    expect(settleGridItem(items, { ...items[2], x: 0, y: 0 })).toEqual(result);
    expect(settleGridItem(items, items[0])).toBe(items);
  });
  it('places on empty space without pushing neighbours', () => {
    const result = settleGridItem(items, { ...items[0], x: 2, y: 15 });
    expect(result.find(item => item.i === 'a')).toMatchObject({ x: 2, y: 15 });
    expect(result.find(item => item.i === 'b')).toEqual(items[1]);
    valid(result);
  });
  it('resolves resize collisions once and preserves unaffected whitespace', () => {
    const result = settleGridItem(items, { ...items[0], w: 12 }, true);
    expect(result.find(item => item.i === 'a')?.w).toBe(12);
    for (const item of result) expect(result.some(other => item.i !== other.i && intersects(item, other))).toBe(false);
    expect(result.find(item => item.i === 'far')).toEqual(items[3]);
  });
});
