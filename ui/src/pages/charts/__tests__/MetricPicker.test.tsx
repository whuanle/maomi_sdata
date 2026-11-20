import { fireEvent, render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { MetricPicker } from '../MetricPicker';
import { queryChartMetrics, queryChartMetricDetails } from '@/api/chartMetrics';

vi.mock('@/api/chartMetrics', () => ({ queryChartMetrics: vi.fn(), queryChartMetricDetails: vi.fn() }));
const range = { from: '2026-11-19T00:00:00Z', to: '2026-11-19T01:00:00Z' };
describe('MetricPicker', () => {
  beforeEach(() => {
    vi.mocked(queryChartMetrics).mockReset().mockResolvedValue({ items: [{ name: 'cpu_usage', kinds: ['gauge'] }, { name: 'memory', kinds: ['sum'] }], truncated: false });
    vi.mocked(queryChartMetricDetails).mockReset().mockResolvedValue({ labels: [{ service: 'api', labels: { host: 'node-1' } }], truncated: false });
  });
  it('searches metric names without running or changing until an option is selected', async () => {
    const change = vi.fn();
    render(<MetricPicker value="" index={0} range={range} onChange={change} onFilter={vi.fn()} />);
    fireEvent.click(screen.getByTestId('metric-picker-0'));
    await screen.findByRole('option', { name: /cpu_usage/ });
    fireEvent.change(screen.getByRole('textbox', { name: '搜索指标…' }), { target: { value: 'cpu' } });
    expect(screen.queryByRole('option', { name: /memory/ })).toBeNull();
    expect(change).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('option', { name: /cpu_usage/ }));
    expect(change).toHaveBeenCalledWith('cpu_usage');
    expect(queryChartMetrics).toHaveBeenCalledWith(range);
  });
  it('keeps browser selection private on cancel and applies it only on confirmation', async () => {
    const change = vi.fn();
    render(<MetricPicker value="" index={0} range={range} onChange={change} onFilter={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '浏览指标' }));
    let dialog = await screen.findByRole('dialog', { name: '浏览指标' });
    fireEvent.click(within(dialog).getByRole('button', { name: '列表' }));
    fireEvent.click(await within(dialog).findByRole('option', { name: /cpu_usage/ }));
    expect(await within(dialog).findByText('labels["labels"]["host"]')).toBeInTheDocument();
    expect(change).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: '取消' }));
    expect(change).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '浏览指标' }));
    dialog = await screen.findByRole('dialog', { name: '浏览指标' });
    expect(within(dialog).getByRole('button', { name: '使用此指标' })).toBeDisabled();
    fireEvent.click(await within(dialog).findByRole('option', { name: /memory/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: '使用此指标' }));
    expect(change).toHaveBeenCalledWith('memory');
  });
  it('shows catalog failures and retries', async () => {
    vi.mocked(queryChartMetrics).mockRejectedValueOnce(new Error('catalog failed'));
    render(<MetricPicker value="" index={0} range={range} onChange={vi.fn()} onFilter={vi.fn()} />);
    fireEvent.click(screen.getByTestId('metric-picker-0'));
    expect(await screen.findByRole('alert')).toHaveTextContent('catalog failed');
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    expect(await screen.findByRole('option', { name: /cpu_usage/ })).toBeInTheDocument();
  });
});
