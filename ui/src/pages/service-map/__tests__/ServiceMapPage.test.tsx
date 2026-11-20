import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { ServiceMapPage } from '../ServiceMapPage';
import { loadMapServices, loadServiceMap, loadServiceMapExamples } from '@/api/serviceMap';
import type { MapResult } from '../serviceMapModel';

vi.mock('@/api/serviceMap', () => ({ loadMapServices: vi.fn(), loadServiceMap: vi.fn(), loadServiceMapExamples: vi.fn() }));
vi.mock('@/pages/search/WhereSuggestInput', () => ({ WhereSuggestInput: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => <input aria-label="query" value={value} onChange={(event) => onChange(event.target.value)} /> }));
vi.mock('@/pages/search/TraceDetailDrawer', () => ({ TraceDetailDrawer: ({ row }: { row: Record<string, unknown> | null }) => row ? <div>opened:{String(row.trace_id)}</div> : null }));
vi.mock('../ServiceMapGraph', () => ({ ServiceMapGraph: ({ onSelect }: { onSelect: (selection: { source: string; target?: string }) => void }) => <button onClick={() => onSelect({ source: 'api', target: 'db' })}>edge</button> }));
vi.mock('@/design-system/Chart', () => ({ Chart: ({ onEvents }: { onEvents?: { click: (event: { data: number[] }) => void } }) => <button data-testid="timeline" onClick={() => onEvents?.click({ data: [Date.parse('2026-11-19T00:00:00Z'), 2] })}>time bucket</button> }));
const metric = { source: 'api', target: '', calls: 4, traces: 2, errors: 1, duration: 200, firstSeen: '2026-11-19T00:00:00Z', lastSeen: '2026-11-19T00:00:30Z' };
const result: MapResult = { nodes: [metric], edges: [{ ...metric, target: 'db' }], buckets: [], totalNodes: 1, totalEdges: 1, sampledTraces: 2, matchedTraces: 4, missingParents: 1, roots: 2, truncated: false };
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(loadMapServices).mockResolvedValue({ services: ['api', 'db'], truncated: false });
  vi.mocked(loadServiceMap).mockResolvedValue(result);
  vi.mocked(loadServiceMapExamples).mockResolvedValue({ rows: [{ trace_id: 'real-trace', ts: metric.firstSeen, name: 'entry' }], truncated: false });
});
function mount() { render(<MemoryRouter initialEntries={['/service-map?from=2026-11-19T00:00:00Z&to=2026-11-19T01:00:00Z&sampling=50']}><ServiceMapPage /></MemoryRouter>); }
describe('ServiceMapPage', () => {
  it('keeps selectors compact and separates responsive query and execution groups', async () => {
    mount();
    await screen.findByText('edge');
    expect(screen.getByTestId('map-service')).toHaveClass('w-48', 'shrink-0');
    expect(screen.getByTestId('map-syntax')).toHaveClass('w-24', 'shrink-0');
    expect(screen.getByTestId('map-color')).toHaveClass('w-48', 'shrink-0');
    expect(screen.getByTestId('map-toolbar')).toHaveClass('xl:flex-row');
    expect(screen.getByTestId('map-query-controls')).toHaveClass('min-w-0', 'flex-1');
    expect(screen.getByTestId('map-execution-controls')).toHaveClass('shrink-0', 'flex-wrap');
  });
  it('fills remaining workspace height and scrolls details without stretching the graph', async () => {
    mount();
    await screen.findByText('edge');
    expect(screen.getByTestId('map-workspace')).toHaveClass('h-full', 'min-h-0');
    expect(screen.getByTestId('map-timeline')).toHaveClass('h-25');
    expect(screen.getByTestId('map-canvas-layout')).toHaveClass('flex-1', 'min-h-[360px]', 'xl:grid-rows-[minmax(0,1fr)]');
    expect(screen.getByTestId('map-canvas')).toHaveClass('min-h-0', 'overflow-hidden');
    expect(screen.getByTestId('map-details')).toHaveClass('min-h-0', 'overflow-auto', 'xl:max-h-none');
  });
  it('renders the executed sample window and drills into a real trace', async () => {
    mount();
    await screen.findByText('edge');
    expect(loadServiceMap).toHaveBeenCalledWith(expect.objectContaining({ sampling: 50, from: '2026-11-19T00:00:00Z' }));
    expect(screen.getByText(/1 个片段未找到/)).toBeTruthy();
    fireEvent.click(screen.getByText('edge'));
    fireEvent.click(await screen.findByRole('button', { name: 'real-trace' }));
    expect(screen.getByText('opened:real-trace')).toBeTruthy();
    expect(loadServiceMapExamples).toHaveBeenCalledWith(expect.objectContaining({ sampling: 50 }), { source: 'api', target: 'db' });
  });
  it('zooms a time bucket using the executed conditions, not edited drafts', async () => {
    mount();
    await screen.findByText('edge');
    fireEvent.change(screen.getByLabelText('query'), { target: { value: 'unexecuted draft' } });
    fireEvent.click(screen.getByTestId('timeline'));
    await waitFor(() => expect(loadServiceMap).toHaveBeenLastCalledWith(expect.objectContaining({
      query: '', sampling: 50, from: '2026-11-19T00:00:00.000Z', to: '2026-11-19T00:01:30.000Z',
    })));
  });
  it('ignores an older response and retries a failed execution', async () => {
    let finish!: (value: MapResult) => void;
    vi.mocked(loadServiceMap).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    mount();
    fireEvent.change(screen.getByLabelText('query'), { target: { value: 'status:error' } });
    fireEvent.click(screen.getByRole('button', { name: '运行' }));
    await screen.findByText('edge');
    finish({ ...result, nodes: [] });
    await waitFor(() => expect(screen.getByText('edge')).toBeTruthy());
    vi.mocked(loadServiceMap).mockRejectedValueOnce(new Error('map failed'));
    fireEvent.click(screen.getByRole('button', { name: '运行' }));
    await screen.findByText('map failed');
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    await screen.findByText('edge');
    expect(screen.queryByText('map failed')).toBeNull();
  });
});
