import { act, fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import '@/i18n';
import { PanelCard } from '../PanelCard';
import { PanelEditorDrawer } from '../PanelEditorDrawer';
import { previewPanel } from '@/api/dashboards';
import { listMetricNames } from '@/api/metrics';
import type { PanelConfig } from '@/api/types';

vi.mock('@/api/dashboards', () => ({ previewPanel: vi.fn() }));
vi.mock('@/api/metrics', () => ({ listMetricNames: vi.fn() }));
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div /> }));
const panel: PanelConfig = { id: 'notes', title: 'Notes', type: 'markdown', markdown: '# Notes', layout: { w: 6, h: 4 } };
beforeEach(() => vi.clearAllMocks());

it('renders persisted Markdown regardless of loading and stale data errors', () => {
  render(<PanelCard config={panel} loading data={{ id: panel.id, series: [], columns: [], rows: [], elapsedMs: 12, error: 'stale error' }} />);
  expect(screen.getByRole('heading', { name: 'Notes' })).toBeInTheDocument();
  expect(screen.queryByText('stale error')).not.toBeInTheDocument();
  expect(screen.queryByText('12ms')).not.toBeInTheDocument();
});

it('edits persisted Markdown locally and saves without a query', async () => {
  const saved = vi.fn();
  render(<PanelEditorDrawer open onOpenChange={vi.fn()} initial={panel} onSaved={saved} />);
  fireEvent.change(screen.getByRole('textbox', { name: 'Markdown 内容' }), { target: { value: '# Updated\n\n**Healthy**' } });
  expect(screen.getByRole('heading', { name: 'Updated' })).toBeInTheDocument();
  await act(async () => { await new Promise((resolve) => setTimeout(resolve, 650)); });
  expect(previewPanel).not.toHaveBeenCalled();
  expect(listMetricNames).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: '保存面板' }));
  expect(saved).toHaveBeenCalledWith(expect.objectContaining({ type: 'markdown', markdown: '# Updated\n\n**Healthy**', query: undefined }));
});
