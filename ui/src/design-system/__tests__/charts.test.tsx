import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { withChartTheme } from '../chartTheme';
import { buildTimeSeriesOption, TimeSeriesChart, timeSeriesNames } from '../TimeSeriesChart';
import { formatChartValue } from '../chartFormat';
import type { CssThemeColors } from '../theme';

vi.mock('../Chart', () => ({ Chart: () => <div data-testid="chart" /> }));

const colors: CssThemeColors = {
  mode: 'dark', text: 'white', textSecondary: 'silver', textFaint: 'gray',
  border: 'dimgray', surface: 'black', surface2: 'gray', bg: 'black', primary: 'purple', accent: 'green',
  success: 'green', warning: 'orange', error: 'red',
};
const series = [{ name: 'cpu', labels: '{"host":"a"}', points: [{ timestampMs: 1, value: 2 }, { timestampMs: 2, value: null }] }];

describe('图表主题', () => {
  it('合并嵌套配置及数组，保留显式覆盖且不修改源配置', () => {
    const option = { legend: { textStyle: { fontSize: 10 } }, xAxis: [{ type: 'time' as const, axisLabel: { color: 'red' } }], tooltip: { trigger: 'axis' as const } };
    const result = withChartTheme(option, colors);
    expect(result.legend).toMatchObject({ textStyle: { color: 'silver', fontSize: 10 } });
    expect(result.xAxis).toMatchObject([{ type: 'time', axisLabel: { color: 'red' } }]);
    expect(result.tooltip).toMatchObject({ backgroundColor: 'black', textStyle: { color: 'white' } });
    expect(option.legend).toEqual({ textStyle: { fontSize: 10 } });
    expect(withChartTheme({ series: [{ type: 'pie', data: [] }] }, colors).xAxis).toBeUndefined();
  });

  it('亮色主题同样作用于图例与提示框', () => {
    const result = withChartTheme({ legend: {}, tooltip: {} }, { ...colors, mode: 'light', textSecondary: 'black', surface: 'white' });
    expect(result.legend).toMatchObject({ textStyle: { color: 'black' } });
    expect(result.tooltip).toMatchObject({ backgroundColor: 'white' });
  });
});

describe('时间序列', () => {
  it('保持 null 缺口且不自动堆叠，格式化轴和提示框单位', () => {
    const option = buildTimeSeriesOption({ series, unit: 'percent', zoom: true });
    expect(option.series).toMatchObject([{ connectNulls: false, data: [[1, 2], [2, null]] }]);
    expect((option.series as { stack?: string }[])[0].stack).toBeUndefined();
    const axis = option.yAxis as { axisLabel: { formatter: (v: number) => string } };
    expect(axis.axisLabel.formatter(2)).toBe('2.0%');
    const tooltip = option.tooltip as { valueFormatter: (v: number) => string; formatter: (items: unknown[]) => string };
    expect(tooltip.valueFormatter(2)).toBe('2.0%');
    const text = tooltip.formatter([{ seriesName: 'cpu {host=' + 'a'.repeat(150) + '}', value: [1, 2] }]);
    expect(text).toContain('2.0%');
    expect(text.split('\n')[1].length).toBeLessThan(40);
    expect(text).not.toContain('{');
    expect(option.dataZoom).toHaveLength(2);
    expect(buildTimeSeriesOption({ series }).dataZoom).toBeUndefined();
  });

  it('同名序列独立，完整标签不因省略重合', () => {
    expect(timeSeriesNames([...series, ...series])).toEqual(['cpu {host=a}', 'cpu {host=a} (#2)']);
    expect(formatChartValue(Infinity)).toBe('-');
    expect(formatChartValue(-1500, 'ms')).toBe('-1.50s');
  });

  it('无有效点显示空态，有数据绘图', () => {
    const view = render(<TimeSeriesChart series={[]} />);
    expect(screen.getByText('暂无数据')).toBeInTheDocument();
    view.rerender(<TimeSeriesChart series={series} />);
    expect(screen.getByTestId('chart')).toBeInTheDocument();
  });
});
