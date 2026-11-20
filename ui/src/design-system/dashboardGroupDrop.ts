import type { GridItem } from './dashboardGridLayout';

/** 中央区域收纳组件，边缘仍保留普通排序；容器不能嵌套. */
export function groupDropTarget(items: GridItem[], moved: GridItem, groupIds: string[]): string | undefined {
  if (groupIds.includes(moved.i)) return undefined;
  const x = moved.x + moved.w / 2;
  const y = moved.y + moved.h / 2;
  return items.find(item => groupIds.includes(item.i) && item.i !== moved.i
    && x >= item.x + item.w / 4 && x <= item.x + item.w * 3 / 4
    && y >= item.y + item.h / 4 && y <= item.y + item.h * 3 / 4)?.i;
}
