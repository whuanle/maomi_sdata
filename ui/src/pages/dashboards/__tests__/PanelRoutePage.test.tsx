import { fireEvent, render, screen, waitFor, act } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import '@/i18n';
import { PanelRoutePage } from '../PanelRoutePage';
import { DashboardSession } from '../DashboardSession';
import { getDashboard, previewPanel, saveDashboard } from '@/api/dashboards';
import { useAuthStore } from '@/store/auth';
import type { PanelConfig } from '@/api/types';

vi.mock('@/api/dashboards', async original => ({
  ...(await original<typeof import('@/api/dashboards')>()),
  getDashboard: vi.fn(), previewPanel: vi.fn(), saveDashboard: vi.fn(),
}));
vi.mock('../ChartPanelEditor', () => ({
  ChartPanelEditor: ({ initial, onClose, onApply, onPreview }: { initial?: PanelConfig; onClose: () => void; onApply: (panel: PanelConfig) => void; onPreview?: () => void }) =>
    <div><span>Editor {initial?.title ?? 'New'}</span><input aria-label="Local draft" />
      <button onClick={onPreview}>Preview editor</button>
      <button onClick={() => { onApply({ ...initial!, title: 'Updated' }); onClose(); }}>Apply editor</button></div>,
}));
const member: PanelConfig = { id: 'member', title: 'Member', type: 'markdown', markdown: 'Member content' };
const config = { version: 3, refreshSeconds: 0, panels: [
  { id: 'plain', title: 'Plain', type: 'table', layout: { x: 0, y: 0, w: 6, h: 4 }, query: { kind: 'sql', sql: 'SELECT 1 AS value' } },
  { id: 'group', title: 'Group', type: 'group', layout: { x: 6, y: 0, w: 6, h: 4 }, panels: [member], defaultPanelId: 'member' },
] };
beforeEach(() => {
  vi.clearAllMocks(); useAuthStore.setState({ token: 'test', role: 'admin' });
  vi.mocked(getDashboard).mockResolvedValue({ id: 100, name: 'Dashboard', kind: 'view', description: '', configJson: JSON.stringify(config), createTime: '', updateTime: '' });
  vi.mocked(previewPanel).mockImplementation(async panel => ({ id: panel.id, series: [], columns: ['value'], rows: [[42]], elapsedMs: 1 }));
});
function setup(path: string) {
  const router = createMemoryRouter([{ path: '/dashboards/:id', element: <DashboardSession />, children: [
    { index: true, element: <div>Dashboard return</div> },
    { path: 'panels/new', element: <PanelRoutePage /> },
    { path: 'panels/:panelId/edit', element: <PanelRoutePage /> },
    { path: 'panels/:panelId/preview', element: <PanelRoutePage preview /> },
  ] }], { initialEntries: [path] });
  const view = render(<RouterProvider router={router} />);
  return { router, ...view };
}
it('opens saved ordinary and grouped member editors directly after remount', async () => {
  const first = setup('/dashboards/100/panels/plain/edit');
  expect(await screen.findByText('Editor Plain')).toBeInTheDocument();
  first.unmount();
  setup('/dashboards/100/panels/member/edit');
  expect(await screen.findByText('Editor Member')).toBeInTheDocument();
  expect(saveDashboard).not.toHaveBeenCalled();
});
it('opens a group editor without passing children to an input', async () => {
  setup('/dashboards/100/panels/group/edit');
  expect(await screen.findByRole('checkbox', { name: 'Member' })).toBeChecked();
  expect(screen.getByRole('textbox', { name: '分组名称' })).toHaveValue('Group');
});
it('opens readonly preview from a deep link and can remount at the same location', async () => {
  useAuthStore.setState({ role: 'readonly' });
  const first = setup('/dashboards/100/panels/plain/preview');
  expect(await screen.findByText('42')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '编辑' })).not.toBeInTheDocument();
  first.unmount();
  setup('/dashboards/100/panels/member/preview');
  expect(await screen.findByText('Member content')).toBeInTheDocument();
});
it('requires discard confirmation when leaving an edited component for preview', async () => {
  const { router } = setup('/dashboards/100/panels/plain/edit');
  await screen.findByText('Editor Plain');
  fireEvent.change(screen.getByRole('textbox', { name: 'Local draft' }), { target: { value: 'unsaved' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview editor' }));
  expect(await screen.findByRole('dialog')).toBeInTheDocument();
  expect(router.state.location.pathname).toContain('/edit');
  fireEvent.click(screen.getByRole('button', { name: '取消' }));
  expect(screen.getByRole('textbox', { name: 'Local draft' })).toHaveValue('unsaved');
});
it('shows an invalid id message and supports direct return without history', async () => {
  const { router } = setup('/dashboards/100/panels/missing/edit');
  expect(await screen.findByRole('alert')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '返回' }));
  await waitFor(() => expect(router.state.location.pathname).toBe('/dashboards/100'));
});
it('uses URLs for different creation flows without saving anything', async () => {
  const { router } = setup('/dashboards/100/panels/new?kind=explorer');
  expect(await screen.findByText('Editor New')).toBeInTheDocument();
  await act(async () => { await router.navigate('/dashboards/100/panels/new?kind=group'); });
  expect(await screen.findByRole('textbox', { name: '分组名称' })).toBeInTheDocument();
  expect(saveDashboard).not.toHaveBeenCalled();
});
