import { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { TableFilterEditor } from '../TableFilterEditor';
import { searchSql } from '@/api/search';
import type { TableFilter } from '../tableBuilder';

vi.mock('@/api/search', async (original) => ({ ...(await original<typeof import('@/api/search')>()), searchSql: vi.fn() }));
function Editor() {
  const [value, setValue] = useState<TableFilter>({ id: 'test', field: 'service', operator: 'eq', value: '' });
  return <TableFilterEditor source="logs" value={value} from="2026-11-17T00:00:00Z" to="2026-11-18T00:00:00Z" onChange={setValue} onRemove={vi.fn()} />;
}
describe('Table value suggestions', () => {
  beforeEach(() => { vi.mocked(searchSql).mockReset().mockResolvedValue({ columns: ['v', 'c'], rows: [['api', 3], ['worker', 2]], elapsedMs: 1, truncated: false }); });
  it('loads real values only when focused and supports keyboard adoption', async () => {
    render(<Editor />);
    expect(searchSql).not.toHaveBeenCalled();
    const input = screen.getByRole('combobox', { name: '筛选值' });
    fireEvent.focus(input);
    await screen.findByRole('option', { name: 'api' });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(input).toHaveValue('api');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });
  it('keeps custom values usable when suggestions fail', async () => {
    vi.mocked(searchSql).mockRejectedValue(new Error('offline'));
    render(<Editor />);
    const input = screen.getByRole('combobox', { name: '筛选值' });
    fireEvent.focus(input);
    await screen.findByText('值建议暂不可用，仍可手动输入');
    fireEvent.change(input, { target: { value: "custom'value" } });
    expect(input).toHaveValue("custom'value");
    await waitFor(() => expect(searchSql).toHaveBeenCalled());
  });
});
