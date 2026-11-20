import { useState, type ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { ChartPanelEditor } from '../ChartPanelEditor';
import { useChartWorkspace } from '@/pages/charts/ChartWorkspaceContext';
import type { PanelConfig } from '@/api/types';
import { SaveChartModal } from '@/pages/charts/SaveChartModal';

function renderEditor(element: ReactNode) {
  return render(<MemoryRouter>{element}</MemoryRouter>);
}

const panel: PanelConfig = { id: 'sql-panel', title: 'Requests', type: 'line', query: { kind: 'sql', sql: 'SELECT $value AS value', parameters: { value: 42 } } };
const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' };

vi.mock('@/pages/charts/ChartWorkspace', () => ({
  ChartWorkspace: () => {
    const workspace = useChartWorkspace();
    const [ready, setReady] = useState(false);
    return <>
      <span data-testid="route-tab">{workspace.urlTab ?? 'default'}</span>
      <button onClick={() => workspace.onTabChange?.('table')}>切换表格位置</button>
      <button onClick={() => workspace.onApply?.(panel)}>应用工作区面板</button>
      <button onClick={() => setReady(true)}>模拟运行成功</button>
      <button onClick={() => setReady(false)}>切换未运行页签</button>
      {ready && <SaveChartModal open={false} onOpenChange={() => undefined} name="Requests" sql={panel.query!.sql!} parameters={panel.query!.parameters!} chartType="time_series" source="logs" series={[]} range={range} display="line" showLegend autoGranularity={false} />}
    </>;
  },
}));
vi.mock('../PanelEditorDrawer', () => ({ PanelEditorDrawer: () => <div>旧编辑侧栏</div> }));

describe('ChartPanelEditor', () => {
  it('restores and updates only the chart tab in the route', () => {
    render(<MemoryRouter initialEntries={['/?tab=number&tab.g=member']}><ChartPanelEditor range={range} onApply={vi.fn()} onClose={vi.fn()} /></MemoryRouter>);
    expect(screen.getByTestId('route-tab')).toHaveTextContent('number');
    fireEvent.click(screen.getByRole('button', { name: '切换表格位置' }));
    expect(screen.getByTestId('route-tab')).toHaveTextContent('table');
  });
  it('enables the footer action only for a successful active chart and applies its SQL directly', async () => {
    const onApply = vi.fn();
    const onClose = vi.fn();
    renderEditor(<ChartPanelEditor range={range} onApply={onApply} onClose={onClose} />);
    const apply = screen.getByRole('button', { name: '应用到画布' });
    expect(apply).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '模拟运行成功' }));
    await waitFor(() => expect(apply).toBeEnabled());
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: '切换未运行页签' }));
    await waitFor(() => expect(apply).toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: '模拟运行成功' }));
    await waitFor(() => expect(apply).toBeEnabled());
    fireEvent.click(apply);
    expect(onApply).toHaveBeenCalledOnce();
    expect(onApply.mock.calls[0][0]).toMatchObject({ query: { kind: 'sql', sql: panel.query!.sql, parameters: panel.query!.parameters } });
    expect(onClose).toHaveBeenCalledOnce();
  });
  it('adds directly from the workspace without offering the legacy query drawer', () => {
    const onApply = vi.fn();
    const onClose = vi.fn();
    renderEditor(<ChartPanelEditor range={range} onApply={onApply} onClose={onClose} />);
    expect(screen.queryByText('旧编辑侧栏')).not.toBeInTheDocument();
    expect(screen.queryByText('高级查询 / Markdown')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '应用工作区面板' }));
    expect(onApply).toHaveBeenCalledWith(panel);
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('edits existing SQL panels in the workspace', () => {
    renderEditor(<ChartPanelEditor initial={panel} range={range} onApply={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByRole('button', { name: '应用工作区面板' })).toBeInTheDocument();
    expect(screen.queryByText('旧编辑侧栏')).not.toBeInTheDocument();
  });

  it.each([
    { id: 'metric', title: 'CPU', type: 'line', query: { kind: 'metric', name: 'cpu' } },
    { id: 'markdown', title: 'Notes', type: 'markdown', markdown: 'Notes' },
  ] satisfies PanelConfig[])('keeps the compatibility editor for existing $id panels', (initial) => {
    renderEditor(<ChartPanelEditor initial={initial} range={range} onApply={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByText('旧编辑侧栏')).toBeInTheDocument();
  });
});
