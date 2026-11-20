import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { SampleMatchedEvents } from '../SampleMatchedEvents';

vi.mock('@/pages/search/ResultsTable', () => ({ ResultsTable: (props: { onRowOpen: (row: Record<string, unknown>) => void; onFilterByValue: (field: string, value: unknown) => void }) => <><button onClick={() => props.onRowOpen({ trace_id: 'trace', body: 'message' })}>Open event</button><button onClick={() => props.onFilterByValue('service', 'api')}>Filter event</button></> }));
vi.mock('@/pages/search/LogDetailDrawer', () => ({ LogDetailDrawer: (props: { onFilterOnlyValue: (field: string, value: unknown) => void }) => <div role="dialog"><button onClick={() => props.onFilterOnlyValue('service', 'api')}>Only value</button></div> }));
vi.mock('@/pages/search/TraceDetailDrawer', () => ({ TraceDetailDrawer: (props: { from: string; onFilterByValue: (field: string, value: unknown, domain: 'logs') => void }) => <div role="dialog">{props.from}<button onClick={() => props.onFilterByValue('body', 'error', 'logs')}>Filter related log</button></div> }));
vi.mock('@/pages/rum/RumPage', () => ({ ReplayView: (props: { projectKey: string; sessionId: string; range: { from: string } }) => <div data-testid="replay">{props.projectKey}/{props.sessionId}/{props.range.from}</div> }));
const props = { source: 'logs' as const, range: { from: '2026-11-19T00:00:00Z', to: '2026-11-19T01:00:00Z' }, mode: 'builder' as const, result: { columns: ['service', 'body'], rows: [['api', 'message']], elapsedMs: 1, truncated: false }, loading: false, generation: 1, onFilter: vi.fn() };

describe('SampleMatchedEvents', () => {
  it('shares log details, only-value filtering and resets details on a new query', () => {
    const filter = vi.fn();
    const view = render(<SampleMatchedEvents {...props} onFilter={filter} />);
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Open event' }));
    fireEvent.click(screen.getByRole('button', { name: 'Only value' }));
    expect(filter).toHaveBeenCalledWith('service', 'api', true, 'logs');
    fireEvent.click(screen.getByRole('button', { name: 'Open event' }));
    view.rerender(<SampleMatchedEvents {...props} generation={2} />);
    expect(screen.queryByRole('dialog')).toBeNull();
  });
  it('preserves trace execution time and related-log source', () => {
    const filter = vi.fn();
    render(<SampleMatchedEvents {...props} source="traces" onFilter={filter} />);
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Open event' }));
    expect(screen.getByRole('dialog')).toHaveTextContent(props.range.from);
    fireEvent.click(screen.getByRole('button', { name: 'Filter related log' }));
    expect(filter).toHaveBeenCalledWith('body', 'error', false, 'logs');
  });
  it('shows metric details and session replay with project identity', () => {
    const view = render(<SampleMatchedEvents {...props} source="metrics" result={{ ...props.result, columns: ['name', 'value'], rows: [['cpu', 12]] }} />);
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    fireEvent.click(screen.getByText('cpu'));
    expect(screen.getByRole('dialog')).toHaveTextContent('12');
    view.rerender(<SampleMatchedEvents {...props} generation={2} source="sessions" result={{ ...props.result, columns: ['project_key', 'session_id'], rows: [['project', 'session']] }} />);
    fireEvent.click(screen.getByText('session'));
    expect(screen.getByTestId('replay')).toHaveTextContent('project/session/' + props.range.from);
  });
  it('distinguishes SQL unavailability, loading and sample failures', () => {
    const view = render(<SampleMatchedEvents {...props} mode="sql" />);
    fireEvent.click(screen.getByRole('button', { name: /匹配事件样本/ }));
    expect(screen.queryByRole('button', { name: 'Open event' })).toBeNull();
    view.rerender(<SampleMatchedEvents {...props} loading />);
    expect(screen.getByRole('status')).toBeInTheDocument();
    view.rerender(<SampleMatchedEvents {...props} error="sample failed" />);
    expect(screen.getByRole('alert')).toHaveTextContent('sample failed');
  });
});
