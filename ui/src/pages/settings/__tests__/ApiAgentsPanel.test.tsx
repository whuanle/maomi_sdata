import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { ApiAgentsPanel } from '../ApiAgentsPanel';
import { fetchApiKeys, manageApiKey } from '@/api/apiKeys';

vi.mock('@/api/apiKeys', () => ({ fetchApiKeys: vi.fn(), manageApiKey: vi.fn() }));
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchApiKeys).mockResolvedValue([]);
  vi.mocked(manageApiKey).mockResolvedValue({ item: {} as never, secret: 'sdi_once-only-secret' });
});
const item = (kind: 'ingestion' | 'agent', canReveal = true) => ({
  id: kind, kind, canReveal, name: kind + '-test', prefix: 'masked',
  enabled: true, createTime: '2026-11-20T00:00:00Z', updateTime: null,
  createUserId: '1', updateUserId: '1',
});
describe('ApiAgentsPanel', () => {
  it.each(['ingestion', 'agent'] as const)('reveals %s keys only on explicit request and allows viewing again', async kind => {
    vi.mocked(fetchApiKeys).mockResolvedValue([item(kind)]);
    render(<ApiAgentsPanel />);
    const view = await screen.findByRole('button', { name: /查看 Key|View key/ });
    expect(manageApiKey).not.toHaveBeenCalled();
    fireEvent.click(view);
    expect(await screen.findByText('sdi_once-only-secret')).toBeTruthy();
    expect(manageApiKey).toHaveBeenCalledWith({ kind, id: kind, action: 'reveal' });
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    fireEvent.click(view);
    expect(await screen.findByText('sdi_once-only-secret')).toBeTruthy();
    expect(manageApiKey).toHaveBeenCalledTimes(2);
  });
  it('explains legacy keys and requires confirmation before resetting', async () => {
    vi.mocked(fetchApiKeys).mockResolvedValue([item('ingestion', false)]);
    render(<ApiAgentsPanel />);
    expect(await screen.findByRole('button', { name: /查看 Key|View key/ })).toBeDisabled();
    expect(screen.getByText(/旧 Key 需先重置|Reset this legacy key/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /重置 Key|Reset key/ }));
    expect(manageApiKey).not.toHaveBeenCalled();
    const confirmation = await screen.findByRole('dialog');
    expect(confirmation).toHaveTextContent(/立即失效|stops working immediately/);
    fireEvent.click(within(confirmation).getByRole('button', { name: /重置 Key|Reset key/ }));
    await waitFor(() => expect(manageApiKey).toHaveBeenCalledWith({ kind: 'ingestion', id: 'ingestion', action: 'rotate' }));
  });
  it('does not reopen a dismissed dialog when a reveal response arrives late', async () => {
    vi.mocked(fetchApiKeys).mockResolvedValue([item('agent')]);
    let resolve!: (value: Awaited<ReturnType<typeof manageApiKey>>) => void;
    vi.mocked(manageApiKey).mockImplementationOnce(() => new Promise(done => { resolve = done; }));
    render(<ApiAgentsPanel />);
    fireEvent.click(await screen.findByRole('button', { name: /查看 Key|View key/ }));
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await act(async () => { resolve({ item: item('agent'), secret: 'late-secret' }); });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText('late-secret')).toBeNull();
  });
  it('shows separate key purposes and placeholder-only connection examples', async () => {
    render(<ApiAgentsPanel />);
    expect(await screen.findByText('Ingestion API keys')).toBeTruthy();
    expect(screen.getByText('Agent access keys')).toBeTruthy();
    expect(screen.getByText(/<INGESTION_KEY>/)).toBeTruthy();
    expect(screen.getByText(/<AGENT_KEY>/)).toBeTruthy();
    expect(fetchApiKeys).toHaveBeenCalled();
  });
  it('shows all 26 tools grouped with the discovery-to-analysis workflow', async () => {
    render(<ApiAgentsPanel />);
    const groups = await screen.findByTestId('agent-tool-groups');
    expect(within(groups).getAllByRole('heading')).toHaveLength(6);
    for (const tool of ['list_tables', 'describe_table', 'aggregate_data', 'compare_periods', 'list_metric_labels', 'query_iot_series', 'summarize_rum']) {
      expect(groups).toHaveTextContent(tool);
    }
    expect(screen.getByText(/26 .*工具|26 read-only tools/)).toBeTruthy();
    expect(screen.getByText(/建议让 AI|Suggested AI workflow/)).toBeTruthy();
  });
  it('creates a key and clears the displayed secret when the dialog closes', async () => {
    render(<ApiAgentsPanel />);
    fireEvent.click(screen.getAllByRole('button', { name: /新建 Key|Create key/ })[0]);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'collector' } });
    const createButtons = screen.getAllByRole('button', { name: /新建 Key|Create key/ });
    fireEvent.click(createButtons[createButtons.length - 1]);
    expect(await screen.findByText('sdi_once-only-secret')).toBeTruthy();
    await waitFor(() => expect(manageApiKey).toHaveBeenCalledWith({ kind: 'ingestion', action: 'create', name: 'collector' }));
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => expect(screen.queryByText('sdi_once-only-secret')).toBeNull());
  });
});
