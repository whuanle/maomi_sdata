import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DashboardGrid, type DashboardGridItem } from '@/design-system/DashboardGrid';
import * as gridLayout from '@/design-system/dashboardGridLayout';

vi.mock('react-grid-layout', async original => ({
  ...(await original<typeof import('react-grid-layout')>()),
  useContainerWidth: () => ({ width: 1200, mounted: true, containerRef: { current: null } }),
}));

function Canvas({ group = false, onDrop = () => undefined, onHover = () => undefined }: { group?: boolean; onDrop?: (panelId: string, groupId: string) => void; onHover?: (groupId?: string) => void }) {
  const [items, setItems] = useState<DashboardGridItem[]>([
    { i: 'a', x: 0, y: 0, w: 6, h: 2 }, { i: 'b', x: 6, y: 0, w: 6, h: 2 },
  ]);
  return <><output data-testid="layout">{JSON.stringify(items)}</output><DashboardGrid items={items} editable onChange={setItems} groupIds={group ? ['a'] : []} onGroupDrop={group ? onDrop : undefined} onGroupHover={onHover}>
    {items.map(item => <div key={item.i} data-testid={item.i}><div className="dashboard-drag-handle">{item.i}</div></div>)}
  </DashboardGrid></>;
}

describe('real grid drag events', () => {
  it('highlights a central group target and drops only on release', () => {
    const drop = vi.fn();
    const hover = vi.fn();
    render(<Canvas group onDrop={drop} onHover={hover} />);
    const node = screen.getByTestId('b');
    Object.defineProperty(node, 'offsetParent', { configurable: true, value: node.parentElement });
    node.getBoundingClientRect = () => ({ x: 606, y: 0, left: 606, top: 0, width: 594, height: 204, right: 1200, bottom: 204, toJSON: () => ({}) });
    fireEvent.mouseDown(screen.getByText('b', { exact: true }), { clientX: 650, clientY: 20, button: 0 });
    fireEvent.mouseMove(document, { clientX: 44, clientY: 20, buttons: 1 });
    expect(hover).toHaveBeenLastCalledWith('a');
    expect(drop).not.toHaveBeenCalled();
    expect(screen.getByTestId('a').style.transform).toContain('translate(0px,0px)');
    fireEvent.mouseUp(document, { clientX: 44, clientY: 20 });
    expect(drop).toHaveBeenCalledExactlyOnceWith('b', 'a');
    expect(hover).toHaveBeenLastCalledWith(undefined);
  });
  it('inserts the second component before the first without persisting overlap', () => {
    const settle = vi.spyOn(gridLayout, 'settleGridItem');
    render(<Canvas />);
    for (const id of ['a', 'b']) {
      const node = screen.getByTestId(id);
      Object.defineProperty(node, 'offsetParent', { configurable: true, value: node.parentElement });
      node.getBoundingClientRect = () => ({ x: id === 'b' ? 606 : 0, y: 0, left: id === 'b' ? 606 : 0, top: 0, width: 594, height: 204, right: id === 'b' ? 1200 : 594, bottom: 204, toJSON: () => ({}) });
    }
    fireEvent.mouseDown(screen.getByText('b', { exact: true }), { clientX: 650, clientY: 20, button: 0 });
    fireEvent.mouseMove(document, { clientX: 44, clientY: 20, buttons: 1 });
    // 松手前邻居已经让位，但草稿只在松手后提交.
    expect(screen.getByTestId('a').style.transform).toContain('translate(606px,0px)');
    expect(JSON.parse(screen.getByTestId('layout').textContent!).map((item: DashboardGridItem) => item.i)).toEqual(['a', 'b']);
    const calculations = settle.mock.calls.length;
    for (let index = 0; index < 30; index++) {
      fireEvent.mouseMove(document, { clientX: 44 + index % 10, clientY: 20, buttons: 1 });
    }
    expect(settle).toHaveBeenCalledTimes(calculations);
    for (let index = 0; index < 3; index++) {
      fireEvent.mouseMove(document, { clientX: 650, clientY: 20, buttons: 1 });
      expect(screen.getByTestId('a').style.transform).toContain('translate(0px,0px)');
      fireEvent.mouseMove(document, { clientX: 44, clientY: 20, buttons: 1 });
      expect(screen.getByTestId('a').style.transform).toContain('translate(606px,0px)');
    }
    fireEvent.mouseUp(document, { clientX: 44, clientY: 20 });
    const result = JSON.parse(screen.getByTestId('layout').textContent!);
    expect(result.map((item: DashboardGridItem) => item.i)).toEqual(['b', 'a']);
    expect(result.map((item: DashboardGridItem) => item.x)).toEqual([0, 6]);
    expect(screen.getByTestId('b').style.transform).toContain('translate(0px,0px)');
    expect(screen.getByTestId('a').style.transform).toContain('translate(606px,0px)');
  });
});
