import { useRef, useState, type ReactNode } from 'react';
import { GridLayout, noCompactor, useContainerWidth, type EventCallback } from 'react-grid-layout';
import { settleGridItem } from './dashboardGridLayout';
import { groupDropTarget } from './dashboardGroupDrop';

import 'react-grid-layout/css/styles.css';
import './DashboardGrid.css';

const dragCompactor = { ...noCompactor, allowOverlap: true };

export interface DashboardGridItem {
  i: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

/** 仅此适配层允许布局引擎生成运行时定位样式. */
export function DashboardGrid({ items, editable = false, onChange, groupIds = [], onGroupDrop, onGroupHover, children }: {
  items: DashboardGridItem[];
  editable?: boolean;
  onChange?: (items: DashboardGridItem[]) => void;
  groupIds?: string[];
  onGroupDrop?: (panelId: string, groupId: string) => void;
  onGroupHover?: (groupId?: string) => void;
  children: ReactNode;
}) {
  const { width, containerRef, mounted } = useContainerWidth();
  const narrow = width < 720;
  const visibleItems = narrow ? items.map((item, index) => ({ ...item, x: 0, y: items.slice(0, index).reduce((sum, value) => sum + value.h, 0), w: 12 })) : items;
  // 即使落点被拒绝、坐标未变，也让受控布局重新同步，且不重挂载图表和 Tab.
  const [revision, setRevision] = useState(0);
  const snapshot = useRef(items);
  const hoveredGroup = useRef<string | undefined>(undefined);
  const hoverGroup = (groupId?: string) => {
    if (hoveredGroup.current === groupId) return;
    hoveredGroup.current = groupId;
    onGroupHover?.(groupId);
  };
  const previewCache = useRef<{ key: string; positions: Map<string, DashboardGridItem> } | null>(null);
  const start = () => {
    snapshot.current = items.map(item => ({ ...item }));
    previewCache.current = null;
  };
  const preview: EventCallback = (layout, _old, item, placeholder) => {
    if (narrow || !item) return;
    const target = onGroupDrop ? groupDropTarget(snapshot.current, item, groupIds) : undefined;
    hoverGroup(target);
    const key = `${item.i}:${item.x}:${item.y}:${item.w}:${item.h}:${target ?? ''}`;
    if (previewCache.current?.key !== key) {
      const next = target ? snapshot.current : settleGridItem(snapshot.current, item);
      previewCache.current = { key, positions: new Map(next.map(value => [value.i, value])) };
    }
    const positions = previewCache.current.positions;
    // 回调发生在引擎提交本帧布局之前；保留拖动项跟手，只预览邻居和落位框.
    for (const current of layout) {
      const position = positions.get(current.i);
      if (position && current.i !== item.i) {
        current.x = position.x;
        current.y = position.y;
      }
    }
    const destination = target ? item : positions.get(item.i);
    if (placeholder && destination) {
      placeholder.x = destination.x;
      placeholder.y = destination.y;
    }
  };
  const commit = (resize: boolean): EventCallback => (_layout, _old, item) => {
    if (!narrow && item) {
      const target = !resize && onGroupDrop ? groupDropTarget(snapshot.current, item, groupIds) : undefined;
      hoverGroup(undefined);
      if (target) onGroupDrop?.(item.i, target);
      else onChange?.(settleGridItem(snapshot.current, item, resize));
      setRevision(value => value + 1);
    }
  };
  return (
    <div ref={containerRef} className="dashboard-grid min-w-0">
      {mounted && <GridLayout
        width={width}
        compactor={dragCompactor}
        layout={visibleItems.map((item) => ({ ...item, moved: revision % 2 === 1, minW: Math.min(item.w, 2), minH: Math.min(item.h, 2), maxH: 8 }))}
        gridConfig={{ cols: 12, rowHeight: 96, margin: [12, 12], containerPadding: [0, 0] }}
        dragConfig={{ enabled: editable && !narrow, handle: '.dashboard-drag-handle', cancel: 'button,input,select,textarea' }}
        resizeConfig={{ enabled: editable && !narrow, handles: ['se'] }}
        onDragStart={start}
        onDrag={preview}
        onResizeStart={start}
        onDragStop={commit(false)}
        onResizeStop={commit(true)}
      >{children}</GridLayout>}
    </div>
  );
}
