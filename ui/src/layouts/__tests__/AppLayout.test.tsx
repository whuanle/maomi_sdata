import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { AppLayout } from '../AppLayout';

vi.mock('../components/AppSider', () => ({ AppSider: () => <aside /> }));

describe('AppLayout spacing', () => {
  it.each([
    ['/dashboards/100', false],
    ['/dashboards/100?edit=1', false],
    ['/dashboards', true],
    ['/charts', true],
    ['/dashboards/100/other', true],
  ])('%s keeps padding only outside dashboard details', (path, padded) => {
    render(<MemoryRouter initialEntries={[path]}><Routes>
      <Route element={<AppLayout />}><Route path="*" element={<div>content</div>} /></Route>
    </Routes></MemoryRouter>);
    expect(screen.getByRole('main').classList.contains('p-6')).toBe(padded);
    expect(screen.getByRole('main')).toHaveClass('overflow-auto');
  });
});
