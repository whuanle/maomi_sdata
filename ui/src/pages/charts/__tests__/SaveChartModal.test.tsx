import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { SaveChartModal } from '../SaveChartModal';
import { getDashboard, queryDashboards, saveDashboard } from '@/api/dashboards';

vi.mock('@/api/dashboards', () => ({ getDashboard: vi.fn(), queryDashboards: vi.fn(), saveDashboard: vi.fn() }));
const original = { version: 2, time: { preset: '6h' }, refreshSeconds: 30, custom: 'retained', panels: [{ id: 'existing', title: 'Existing', type: 'stat', layout: { x: 0, y: 0, w: 6, h: 4 } }] };
const item = { id: 1, name: 'Production', kind: 'view' as const, description: 'Keep description', configJson: JSON.stringify(original), createTime: '', updateTime: '' };
const props = { open: true, onOpenChange: vi.fn(), name: 'Errors', sql: 'SELECT count(*) AS value FROM {{logs}} WHERE severity = $p0', parameters: { p0: "ERROR'quoted" }, chartType: 'time_series' as const, source: 'logs' as const, series: [{ id: 'a', aggFunc: 'count' as const, field: '', where: '', alias: '' }], range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' }, display: 'line' as const, showLegend: false, autoGranularity: true };
async function selectDashboard() {
  const select = await screen.findByRole('combobox', { name: '仪表盘 *' });
  await waitFor(() => expect(select).not.toBeDisabled());
  fireEvent.keyDown(select, { key: 'ArrowDown' });
  fireEvent.click(await screen.findByRole('option', { name: 'Production' }));
}
beforeEach(() => {
  vi.clearAllMocks();
  HTMLElement.prototype.scrollIntoView = vi.fn();
  vi.mocked(queryDashboards).mockResolvedValue({ page: 1, pageSize: 200, count: 1, data: [item] });
  vi.mocked(getDashboard).mockResolvedValue(item);
  vi.mocked(saveDashboard).mockResolvedValue({ value: 1 });
});
describe('SaveChartModal', () => {
  it('persists categorical Bar settings, bound parameters and disables time bucketing', async () => {
    const barSettings = { seriesLimit: 5, format: { output: 'number' as const, decimals: 2, thousands: true, compact: false } };
    render(<SaveChartModal {...props} chartType="bar" barSettings={barSettings} barGroupBy={['service']} orderBy="service ASC" />);
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const panel = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!).panels[1];
    expect(panel).toMatchObject({ type: 'bar', query: { parameters: props.parameters, autoGranularity: false },
      explorer: { chartType: 'bar', barSettings, barGroupBy: ['service'], orderBy: 'service ASC', autoGranularity: false } });
  });
  it('persists heatmap distribution config without changing existing panels', async () => {
    const heatmap = { field: 'duration_us' as const, bins: 40 };
    render(<SaveChartModal {...props} chartType="heatmap" source="traces" heatmap={heatmap} />);
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const config = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!);
    expect(config.panels[1]).toMatchObject({ type: 'heatmap', explorer: { heatmap, source: 'traces' }, query: { autoGranularity: true } });
    expect(config.panels[0]).toEqual(original.panels[0]);
  });
  it('persists table settings and keeps the table row limit', async () => {
    const tableSettings = { decimals: 1, rowClick: false, columns: { total: { color: 'warning' as const } } };
    render(<SaveChartModal {...props} chartType="table" autoGranularity={false} tableSettings={tableSettings} tableGroupBy={['service']} />);
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const panel = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!).panels[1];
    expect(panel.type).toBe('table');
    expect(panel.rowLimit).toBe(500);
    expect(panel.explorer).toMatchObject({ tableSettings, tableGroupBy: ['service'] });
    expect(panel.query.autoGranularity).toBe(false);
  });
  it('persists pie display settings and executed series format', async () => {
    const format = { output: 'number' as const, decimals: 3, thousands: false, compact: true };
    const pieSettings = { seriesLimit: 5, format };
    render(<SaveChartModal {...props} chartType="pie" pieSettings={pieSettings} series={[{ ...props.series[0], format }]} />);
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const panel = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!).panels[1];
    expect(panel.type).toBe('pie');
    expect(panel.explorer.pieSettings).toEqual(pieSettings);
    expect(panel.explorer.series[0].format).toEqual(format);
  });
  it('persists number settings and trend query', async () => {
    const numberSettings = { format: { output: 'number' as const, decimals: 1, thousands: true, compact: false }, color: 'warning' as const, rules: [{ id: 'a', operator: 'gte' as const, value: 0, color: 'success' as const }, { id: 'b', operator: 'gte' as const, value: 10, color: 'error' as const }], background: 'area' as const, backgroundColor: 'accent' as const };
    const numberFormats = { a: { ...numberSettings.format, output: 'percentage' as const } };
    const numberTrend = { sql: 'SELECT count(*) AS value FROM {{logs}}', parameters: {} };
    render(<SaveChartModal {...props} chartType="number" series={[{ ...props.series[0], whereSyntax: 'lucene', where: 'severity:ERROR' }]} numberSettings={numberSettings} numberFormats={numberFormats} numberTrend={numberTrend} />);
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const panel = JSON.parse(vi.mocked(saveDashboard).mock.calls[0][0].configJson!).panels[1];
    expect(panel.type).toBe('stat');
    expect(panel.explorer).toMatchObject({ numberSettings, numberFormats, numberTrend });
    expect(panel.query.numberTrend).toEqual(numberTrend);
    expect(panel.visualization).toMatchObject({ kind: 'number', numberSettings, numberFormats });
    expect(panel.visualization).not.toHaveProperty('numberTrend');
    expect(panel.explorer.series[0]).toMatchObject({ whereSyntax: 'lucene', where: 'severity:ERROR' });
  });
  it('requires a dashboard and appends without losing existing config or parameters', async () => {
    const formulas = [{ id: 'f1', expression: 'A / (A + B) * 100', alias: 'Ratio' }];
    render(<SaveChartModal {...props} formulas={formulas} showInputSeries={false} />);
    expect(screen.getByRole('button', { name: '保存到仪表盘' })).toBeDisabled();
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    await waitFor(() => expect(saveDashboard).toHaveBeenCalledOnce());
    const saved = vi.mocked(saveDashboard).mock.calls[0][0];
    expect(saved.expectedConfigJson).toBe(item.configJson);
    expect(saved.description).toBe(item.description);
    const config = JSON.parse(saved.configJson!);
    expect(config.time).toEqual(original.time);
    expect(config.custom).toBe('retained');
    expect(config.panels[0]).toEqual(original.panels[0]);
    expect(config.panels[1].layout).toEqual({ x: 0, y: 4, w: 6, h: 4 });
    expect(config.panels[1].query.parameters).toEqual(props.parameters);
    expect(config.panels[1].query.autoGranularity).toBe(true);
    expect(config.panels[1].showLegend).toBe(false);
    expect(config.panels[1].explorer.formulas).toEqual(formulas);
    expect(config.panels[1].explorer.showInputSeries).toBe(false);
    expect(config.panels[1].explorer.version).toBe(3);
    await waitFor(() => expect(props.onOpenChange).toHaveBeenCalledWith(false));
  });
  it('retains the dialog and selection on a failed save', async () => {
    vi.mocked(saveDashboard).mockRejectedValue(new Error('Concurrent edit'));
    render(<SaveChartModal {...props} />);
    await selectDashboard();
    fireEvent.click(screen.getByRole('button', { name: '保存到仪表盘' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Concurrent edit');
    expect(props.onOpenChange).not.toHaveBeenCalled();
    expect(screen.getByRole('combobox', { name: '仪表盘 *' })).toHaveTextContent('Production');
  });
  it('shows the empty state without allowing a save', async () => {
    vi.mocked(queryDashboards).mockResolvedValue({ page: 1, pageSize: 200, count: 0, data: [] });
    render(<SaveChartModal {...props} />);
    expect(await screen.findByText('请先创建一个仪表盘。')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '保存到仪表盘' })).toBeDisabled();
  });
});
