export interface GridItem { i: string; x: number; y: number; w: number; h: number }

export const visualOrder = (a: GridItem, b: GridItem) => a.y - b.y || a.x - b.x || a.i.localeCompare(b.i);
export const intersects = (a: GridItem, b: GridItem) =>
  a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;

function fit(item: GridItem, occupied: GridItem[], start: { x: number; y: number }): GridItem {
  for (let y = start.y; ; y++) {
    for (let x = y === start.y ? start.x : 0; x <= 12 - item.w; x++) {
      const candidate = { ...item, x, y };
      if (!occupied.some(other => intersects(candidate, other))) return candidate;
    }
  }
}

/** 只重排源与目标之间的视觉区间，尺寸及区间外的主动留白不变. */
export function insertGridItem(items: GridItem[], id: string, targetId: string, after = false): GridItem[] {
  const ordered = [...items].sort(visualOrder);
  const source = ordered.findIndex(item => item.i === id);
  const target = ordered.findIndex(item => item.i === targetId);
  if (source < 0 || target < 0 || source === target) return items;
  const next = ordered.filter(item => item.i !== id);
  next.splice(next.findIndex(item => item.i === targetId) + Number(after), 0, ordered[source]);
  if (next.every((item, index) => item.i === ordered[index].i)) return items;
  const low = Math.min(source, next.findIndex(item => item.i === id));
  const high = Math.max(source, next.findIndex(item => item.i === id));
  let end = high;
  for (;;) {
    const placed = next.slice(0, low);
    const tail = next.slice(end + 1);
    let cursor = { x: ordered[low].x, y: ordered[low].y };
    for (const item of next.slice(low, end + 1)) {
      const position = fit(item, placed, cursor);
      placed.push(position);
      cursor = { x: position.x + position.w, y: position.y };
    }
    const blocked = tail.filter(item => placed.some(other => intersects(item, other)));
    if (!blocked.length) return [...placed, ...tail].sort(visualOrder);
    // 新尺寸组合若占用后续组件的位置，只扩展到真实受影响的组件，避免绕到其后留下大洞.
    end = Math.max(...blocked.map(item => next.findIndex(other => other.i === item.i)));
  }
}

/** 引擎允许临时重叠，提交时仅使用起始快照与最终落点，不累计途中碰撞. */
export function settleGridItem(items: GridItem[], changed: GridItem, resize = false): GridItem[] {
  const original = items.find(item => item.i === changed.i);
  if (!original) return items;
  const moved = { ...changed, x: Math.max(0, Math.min(12 - changed.w, changed.x)), y: Math.max(0, changed.y) };
  if (!resize && original.x === moved.x && original.y === moved.y) return items;
  const collisions = items.filter(item => item.i !== moved.i && intersects(item, moved)).sort((a, b) => {
    const area = (item: GridItem) => (Math.min(item.x + item.w, moved.x + moved.w) - Math.max(item.x, moved.x))
      * (Math.min(item.y + item.h, moved.y + moved.h) - Math.max(item.y, moved.y));
    return area(b) - area(a) || visualOrder(a, b);
  });
  if (!resize && collisions.length) {
    const target = collisions[0];
    const cy = moved.y + moved.h / 2 - target.y - target.h / 2;
    const after = cy > 0 || (cy === 0 && moved.x + moved.w / 2 > target.x + target.w / 2);
    return insertGridItem(items, moved.i, target.i, after);
  }
  const placed = [moved, ...items.filter(item => item.i !== moved.i && !collisions.includes(item))];
  for (const item of collisions.sort(visualOrder)) {
    placed.push(fit(item, placed, { x: item.x, y: item.y }));
  }
  return placed.sort(visualOrder);
}
