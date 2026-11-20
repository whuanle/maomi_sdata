import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { GroupCard } from '../GroupCard';
import { MemoryRouter } from 'react-router';

vi.mock('../PanelCard', () => ({ PanelCardBody: ({ config }: { config: { markdown: string } }) => <p>{config.markdown}</p> }));
describe('Group tabs', () => {
  it('restores selected members from the address on a fresh mount', () => {
    const panels = [{ id: 'a', title: 'A', type: 'markdown' as const, markdown: 'alpha' }, { id: 'b', title: 'B', type: 'markdown' as const, markdown: 'beta' }];
    render(<MemoryRouter initialEntries={['/?tab.g=b']}><GroupCard config={{ id: 'g', title: 'Group', type: 'group', panels, defaultPanelId: 'a' }} data={{}} /></MemoryRouter>);
    expect(screen.getByText('beta')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'B' })).toHaveAttribute('aria-selected', 'true');
  });
  it('switches only the active member and falls back after member removal', () => {
    const panels = [{ id: 'a', title: 'A', type: 'markdown' as const, markdown: 'alpha' }, { id: 'b', title: 'B', type: 'markdown' as const, markdown: 'beta' }];
    const config = { id: 'g', title: 'Group', type: 'group' as const, panels, defaultPanelId: 'b' };
    const { rerender } = render(<MemoryRouter><GroupCard config={config} data={{}} /></MemoryRouter>);
    expect(screen.getByText('beta')).toBeInTheDocument();
    expect(screen.queryByText('alpha')).not.toBeInTheDocument();
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'A' }), { button: 0, ctrlKey: false });
    expect(screen.getByText('alpha')).toBeInTheDocument();
    rerender(<MemoryRouter><GroupCard config={{ ...config, panels: [panels[1]] }} data={{}} /></MemoryRouter>);
    expect(screen.getByText('beta')).toBeInTheDocument();
  });
});
