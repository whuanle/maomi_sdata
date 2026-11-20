import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { SearchAlertsModal } from '../SearchAlertsModal';
import { effectiveSearch } from '../effectiveSearch';
import { alertChannels, listAlerts, previewAlert, saveAlert } from '@/api/alerts';

vi.mock('@/design-system/Chart', () => ({ Chart: () => <div /> }));
vi.mock('@/api/alerts', () => ({
  alertChannels: vi.fn(), listAlerts: vi.fn(), previewAlert: vi.fn(), saveAlert: vi.fn(),
  deleteAlert: vi.fn(), alertHistory: vi.fn(),
}));
const definition = { domain: 'logs' as const, mode: 'lucene' as const, where: 'severity:ERROR', rangeMs: 900000,
  filters: [{ id: 1, field: 'service', op: 'eq' as const, value: 'api' }], denoise: true };

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(listAlerts).mockResolvedValue([]);
  vi.mocked(alertChannels).mockResolvedValue([{ id: '9007199254740993', name: 'Ops', type: 'webhook', enabled: true }]);
  vi.mocked(saveAlert).mockResolvedValue({} as never);
});
describe('Search alerts', () => {
  it('creates a complete frozen condition without automatically previewing or sending', async () => {
    render(<SearchAlertsModal open onOpenChange={() => {}} definition={definition} canWrite />);
    await screen.findByText('暂无告警');
    fireEvent.click(screen.getByRole('button', { name: '新建告警' }));
    fireEvent.change(screen.getByRole('textbox', { name: '告警名称' }), { target: { value: 'Errors' } });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Ops · webhook' }));
    fireEvent.click(screen.getByRole('button', { name: '保存搜索与告警' }));
    await waitFor(() => expect(saveAlert).toHaveBeenCalled());
    const draft = vi.mocked(saveAlert).mock.calls[0][0];
    expect(draft.channels).toEqual(['9007199254740993']);
    expect(draft.condition).toContain('severity NOT IN');
    expect(Object.values(draft.parameters)).toContain('api');
    expect(JSON.parse(draft.definition)).toEqual(definition);
    expect(previewAlert).not.toHaveBeenCalled();
  });
  it('hides write controls for readonly users', async () => {
    render(<SearchAlertsModal open onOpenChange={() => {}} definition={definition} canWrite={false} />);
    await screen.findByText('暂无告警');
    expect(screen.queryByRole('button', { name: '新建告警' })).toBeNull();
    expect(alertChannels).not.toHaveBeenCalled();
  });
  it('keeps projection and condition parameter namespaces collision-free', () => {
    const parameters = { p0: 'projection' };
    const effective = effectiveSearch(definition, parameters);
    expect(effective.parameters.p0).toBe('projection');
    expect(Object.values(effective.parameters)).toContain('ERROR');
    expect(Object.values(effective.parameters)).toContain('api');
  });
});
