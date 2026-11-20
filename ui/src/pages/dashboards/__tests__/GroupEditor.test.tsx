import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { GroupEditor } from '../GroupEditor';

const panels = [
  { id: 'a', title: 'Alpha', type: 'markdown' as const, markdown: 'alpha' },
  { id: 'b', title: 'Beta', type: 'markdown' as const, markdown: 'beta' },
];
describe('Group editor', () => {
  it('creates an empty group with only a name and no tab selection', async () => {
    const apply = vi.fn();
    render(<GroupEditor config={{ version: 2, panels, refreshSeconds: 0 }} onClose={vi.fn()} onApply={apply} />);
    fireEvent.change(screen.getByRole('textbox', { name: '分组名称' }), { target: { value: 'Empty' } });
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(apply).toHaveBeenCalledOnce());
    expect(apply.mock.calls[0][0]).toMatchObject({ title: 'Empty', type: 'group', panels: [] });
  });
  it('adds and orders members when editing an existing group', async () => {
    const apply = vi.fn();
    render(<GroupEditor config={{ version: 3, panels, refreshSeconds: 0 }} initial={{ id: 'g', title: 'Group', type: 'group', panels: [] }} onClose={vi.fn()} onApply={apply} />);
    fireEvent.change(screen.getByRole('textbox', { name: '分组名称' }), { target: { value: 'Overview' } });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Alpha' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Beta' }));
    const row = screen.getAllByText('Beta').find(element => element.parentElement?.querySelector('button'))!.parentElement!;
    fireEvent.click(within(row).getByRole('button', { name: '上移' }));
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(apply).toHaveBeenCalledOnce());
    expect(apply.mock.calls[0][0]).toMatchObject({ title: 'Overview', type: 'group', panels: [panels[1], panels[0]], defaultPanelId: 'b' });
  });
  it('allows removing the final member so the caller can dissolve the container', async () => {
    const apply = vi.fn();
    render(<GroupEditor config={{ version: 3, panels: [], refreshSeconds: 0 }} initial={{ id: 'g', title: 'Group', type: 'group', panels: [panels[0]] }} onClose={vi.fn()} onApply={apply} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Alpha' }));
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(apply).toHaveBeenCalledOnce());
    expect(apply.mock.calls[0][0].panels).toEqual([]);
  });
});
