import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
vi.mock('@/design-system/Chart', () => ({ Chart: () => <div data-testid="mock-chart" /> }));
import { PanelCardBody, formatPanelValue, seriesDisplayName } from '../PanelCard';
import type { PanelConfig, PanelData } from '@/api/types';
import { visualizationFromExplorer } from '@/pages/charts/panelDefinition';

const lineConfig: PanelConfig = { id: 'p1', title: 'CPU', type: 'line', showLegend: true };

const seriesData: PanelData = {
  id: 'p1',
  elapsedMs: 12,
  series: [
    { name: 'cpu_usage', labels: '{"service":"api"}', points: [{ timestampMs: 1, value: 95.5 }, { timestampMs: 2, value: 88 }] },
    { name: 'cpu_usage', labels: '{"service":"db"}', points: [{ timestampMs: 1, value: 42 }] },
  ],
  columns: [],
  rows: [],
};

const tableData: PanelData = {
  id: 'p2',
  elapsedMs: 5,
  series: [],
  columns: ['service', 'total'],
  rows: [['api', 920], ['db', 1143]],
};

describe('formatPanelValue', () => {
  it('按单位格式化', () => {
    expect(formatPanelValue(95.53, 'percent')).toBe('95.5%');
    expect(formatPanelValue(940, 'ms')).toBe('940.0ms');
    expect(formatPanelValue(1500, 'ms')).toBe('1.50s');
    expect(formatPanelValue(29.84, 'ops')).toBe('29.8/s');
    expect(formatPanelValue(1234567, 'short')).toBe('1.23M');
    expect(formatPanelValue(null)).toBe('-');
  });
});

describe('seriesDisplayName', () => {
  it('无标签回退名称，有标签拼接前两组', () => {
    expect(seriesDisplayName({ name: 'm', labels: '{}', points: [] })).toBe('m');
    expect(seriesDisplayName({ name: 'm', labels: '{"service":"api","host":"h1","extra":"x"}', points: [] })).toBe('m {service=api, host=h1, +1}');
  });
});

describe('PanelCardBody', () => {
  it.each(['time_series', 'bar', 'pie', 'heatmap'] as const)('renders %s without Explorer or editor metadata', (kind) => {
    const visualization = visualizationFromExplorer({ chartType: kind, source: 'logs', series: [], range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' }, display: 'line', autoGranularity: false });
    render(<PanelCardBody config={{ ...lineConfig, type: kind === 'time_series' ? 'line' : kind, visualization }} data={{ ...tableData, columns: ['ts_bucket', 'service', 'value'], rows: [['2026-11-18T00:01:00Z', 'api', 3]] }} />);
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
  });
  it('renders Number formulas without editor metadata and keeps unknown editor versions out of runtime', () => {
    const visualization = visualizationFromExplorer({ chartType: 'number', source: 'logs', series: [], formulas: [{ id: 'f', expression: 'A / B', alias: 'Ratio' }], showInputSeries: false, range: { from: '', to: '' }, display: 'line', autoGranularity: false });
    render(<PanelCardBody config={{ ...lineConfig, type: 'stat', visualization, editor: { version: 99 } as unknown as NonNullable<PanelConfig['editor']> }} data={{ ...tableData, columns: ['__series_id', '__formula', 'value'], rows: [['a', false, 20], ['f', true, 40]] }} />);
    expect(screen.getByText('40')).toBeInTheDocument();
    expect(screen.queryByText('20')).toBeNull();
    expect(screen.getByText('Ratio')).toBeInTheDocument();
  });
  it('renders Search without Explorer metadata', () => {
    const visualization = visualizationFromExplorer({ chartType: 'search', source: 'logs', series: [], range: { from: '', to: '' }, display: 'line', autoGranularity: false });
    render(<PanelCardBody config={{ ...lineConfig, type: 'table', visualization }} data={{ ...tableData, columns: ['body', '__identity'], rows: [['event', 1]] }} />);
    expect(screen.getByText('event')).toBeInTheDocument();
    expect(screen.queryByText('__identity')).toBeNull();
  });
  it('renders saved Patterns with trends instead of internal result columns', () => {
    const explorer = { chartType: 'patterns', source: 'logs', series: [], range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' }, display: 'line' as const, autoGranularity: false };
    render(<PanelCardBody config={{ ...lineConfig, type: 'table', explorer }} data={{ ...tableData, columns: ['pattern', 'level', 'count', 'sample', 'trend', 'total_patterns'], rows: [['order <*>', 'info', 7, null, '[1,2]', 1]] }} />);
    expect(screen.getByText('order')).toBeInTheDocument();
    expect(screen.queryByText('total_patterns')).toBeNull();
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
  });
  it('paginates saved Search results without dropping rows after fifty', () => {
    const explorer = { chartType: 'search', source: 'logs', series: [], range: { from: '', to: '' }, display: 'line' as const, autoGranularity: false };
    render(<PanelCardBody config={{ ...lineConfig, type: 'table', explorer }} data={{ ...tableData, columns: ['body', '__identity'], rows: Array.from({ length: 60 }, (_, i) => ['event ' + i, i]) }} />);
    expect(screen.queryByText('__identity')).toBeNull();
    expect(screen.getByText('event 0')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'next page' })).toBeEnabled();
  });
  it('restores categorical Bar panels including negative values and validates SQL results', () => {
    const explorer = { version: 2, chartType: 'bar', source: 'logs', series: [], range: { from: '', to: '' }, display: 'bar' as const, autoGranularity: false,
      barSettings: { format: { output: 'number' as const, decimals: 2, thousands: true, compact: false } } };
    const config = { ...lineConfig, type: 'bar' as const, explorer };
    const view = render(<PanelCardBody config={config} data={{ ...tableData, columns: ['ts_bucket', 'value'], rows: [['category', -5]] }} />);
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
    view.rerender(<PanelCardBody config={config} data={{ ...tableData, columns: ['service', 'total'], rows: [['api', 5]] }} />);
    expect(screen.queryByTestId('mock-chart')).toBeNull();
    expect(screen.getByText('结果需要数值 value 列，其他列作为类别。')).toBeInTheDocument();
  });
  it('restores stable Table column formatting, stripes and header behavior', () => {
    const explorer = { chartType: 'table', source: 'logs', series: [], range: { from: '', to: '' }, display: 'line' as const, autoGranularity: false,
      tableColumns: [{ id: 'group:service', name: 'service', kind: 'group' as const }, { id: 'metric', name: 'total', kind: 'metric' as const }],
      tableSettings: { striped: true, stickyHeader: false, columns: { metric: { format: 'percent' as const, decimals: 0 } } } };
    render(<PanelCardBody config={{ ...lineConfig, type: 'table', explorer }} data={tableData} />);
    expect(screen.getByText('920%')).toBeInTheDocument();
    expect(screen.getAllByRole('row')[2]).toHaveClass('bg-surface-2/50');
    expect(screen.getAllByRole('columnheader')[0]).not.toHaveClass('sticky');
  });

  it('restores pie panels and renders zero results without a misleading chart', () => {
    const explorer = { version: 2, chartType: 'pie', source: 'logs', series: [], range: { from: '', to: '' }, display: 'line' as const, autoGranularity: false,
      pieSettings: { seriesLimit: 1, format: { output: 'number' as const, decimals: 3, thousands: false, compact: false } } };
    const config = { ...lineConfig, type: 'pie' as const, explorer };
    const view = render(<PanelCardBody config={config} data={{ ...tableData, columns: ['service', 'value'], rows: [['api', 7], ['db', 3]] }} />);
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
    view.rerender(<PanelCardBody config={config} data={{ ...tableData, columns: ['value'], rows: [[0]] }} />);
    expect(screen.queryByTestId('mock-chart')).toBeNull();
    expect(screen.getByText('暂无可展示的正数值。')).toBeInTheDocument();
  });
  it('restores distribution heatmaps and retains legacy category heatmaps', () => {
    const explorer = { version: 2, chartType: 'heatmap', source: 'traces', series: [], range: { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' }, display: 'line' as const, autoGranularity: true };
    const config = { ...lineConfig, type: 'heatmap' as const, explorer: { ...explorer, heatmap: { field: 'duration_us' as const, bins: 40 } } };
    const view = render(<PanelCardBody config={config} data={{ ...tableData, columns: ['ts_bucket', 'y_bucket', 'bucket_min', 'bucket_max', 'value', '__bucket_ms'], rows: [['2026-11-18T00:01:00Z', 0, 0, 1000, 3, 60000]] }} />);
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
    view.rerender(<PanelCardBody config={{ ...config, explorer }} data={{ ...tableData, columns: ['ts_bucket', 'service', 'value'], rows: [['2026-11-18T00:01:00Z', 'api', 3]] }} />);
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
  });
  it('restores number labels, format overrides and conditional colors', () => {
    const explorer = { version: 2, chartType: 'number', source: 'logs', series: [{ id: 'a', aggFunc: 'count' as const, field: '', where: '', alias: 'Requests' }], range: { from: '', to: '' }, display: 'line' as const, autoGranularity: false,
      numberSettings: { format: { output: 'number' as const, decimals: 0, thousands: false, compact: false }, color: 'default' as const, background: 'none' as const, rules: [{ id: 'r', operator: 'gte' as const, value: 0.2, color: 'warning' as const }] },
      numberFormats: { a: { output: 'percentage' as const, decimals: 1, thousands: false, compact: false } } };
    render(<PanelCardBody config={{ ...lineConfig, type: 'stat', explorer }} data={{ ...tableData, columns: ['value'], rows: [[0.25]] }} />);
    expect(screen.getByText('25.0%')).toBeInTheDocument();
    expect(screen.getByText('Requests')).toBeInTheDocument();
    expect(screen.getByText('25.0%').parentElement).toHaveClass('text-warning');
  });
  it('时间序列渲染图表', () => {
    render(<PanelCardBody config={lineConfig} data={seriesData} />);
    expect(screen.getByTestId('mock-chart')).toBeInTheDocument();
  });

  it('表格形态渲染行（超过 50 行提示截断）', () => {
    render(<PanelCardBody config={{ ...lineConfig, type: 'table' }} data={tableData} />);
    expect(screen.getByText('api')).toBeInTheDocument();
    expect(screen.getByText('920')).toBeInTheDocument();
  });

  it('错误信息展示', () => {
    render(<PanelCardBody config={lineConfig} data={{ ...tableData, error: '查询执行失败' }} />);
    expect(screen.getByText('查询执行失败')).toBeInTheDocument();
  });

  it('公式面板恢复显示开关并隐藏内部列', () => {
    const explorer = { version: 2, chartType: 'number', source: 'logs', series: [], range: { from: '', to: '' }, display: 'line' as const, autoGranularity: false, formulas: [{ id: 'f', expression: 'A / B', alias: 'Ratio' }], showInputSeries: false };
    const data = { ...tableData, columns: ['__series_id', 'series', '__formula', 'value'], rows: [['a', 'A', false, 20], ['f', 'Ratio', true, 40]] };
    const view = render(<PanelCardBody config={{ ...lineConfig, type: 'stat', explorer }} data={data} />);
    expect(screen.getByText('40')).toBeInTheDocument();
    expect(screen.queryByText('20')).toBeNull();
    view.rerender(<PanelCardBody config={{ ...lineConfig, type: 'table', explorer }} data={data} />);
    expect(screen.queryByText('__series_id')).toBeNull();
    expect(screen.queryByText('__formula')).toBeNull();
    expect(screen.getByText('Ratio')).toBeInTheDocument();
  });

  it('stat 形态取序列末值大数展示', () => {
    render(<PanelCardBody config={{ ...lineConfig, type: 'stat' }} data={seriesData} />);
    expect(screen.getByText('88')).toBeInTheDocument();
  });
});
