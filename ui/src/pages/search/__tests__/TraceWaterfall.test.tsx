import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { EChartsOption } from 'echarts';
import '@/i18n';
import { TraceWaterfall } from '../TraceWaterfall';

const charts = vi.hoisted(() => ({ current: [] as { option: EChartsOption; onEvents: Record<string, (event: unknown) => void> }[] }));
vi.mock('@/design-system/Chart', () => ({ Chart: (props: typeof charts.current[number]) => { charts.current.push(props); return <div />; } }));
const span = { ts: '2026-11-18T12:00:00.123456Z', span_id: 's', service: 'api', name: 'request', duration_us: 1000, status: 'ok',
  events: [{ name: '<event>', timestamp: '2026-11-18T12:00:00.123900Z', attributes: { key: 'value' } }] };

describe('TraceWaterfall', () => {
  it.each([
    [1000, 0, 100, 600, '1.0 ms · request', false],
    [0, 0, 100, 240, '0 µs · request', false],
    [1000, 10, 20, 240, '→ 1.0 ms · request', true],
  ])('条尾重复名称且不超出画布：%sµs / %s–%s / %spx', (durationUs, from, to, width, expected, clipped) => {
    charts.current = [];
    const onSelect = vi.fn();
    const item = { ...span, duration_us: durationUs, events: [] };
    render(<TraceWaterfall spans={[item]} logs={[]} selectedId="s" onSelect={onSelect} onLogSelect={vi.fn()} />);
    act(() => charts.current[charts.current.length - 2].onEvents.datazoom({ start: from, end: to }));
    const chart = charts.current[charts.current.length - 1];
    const axis = chart.option.xAxis as { min: number; max: number };
    const series = (chart.option.series as unknown as {
      data: unknown[];
      renderItem: (params: unknown, api: unknown) => { children: { shape?: { x: number; width: number }; style: { x: number; width: number; text: string; overflow: string; font: string } }[] };
    }[])[0];
    const values = [0, 0, durationUs];
    const geometry = series.renderItem({}, {
      value: (index: number) => values[index],
      coord: ([time]: number[]) => [(time - axis.min) / (axis.max - axis.min) * width * 0.72, 14],
      getWidth: () => width,
    });
    const [bar, label] = geometry.children;
    expect(label.style.text).toBe(expected);
    expect(label.style.x).toBeGreaterThanOrEqual(bar.shape!.x + bar.shape!.width);
    expect(label.style.x + label.style.width).toBeLessThanOrEqual(width);
    expect(label.style.overflow).toBe('truncate');
    expect(label.style.font).toContain('bold');
    expect(label.style.text.startsWith('→')).toBe(clipped);
    chart.onEvents.click({ data: series.data[0] });
    expect(onSelect).toHaveBeenCalledWith(item);
  });

  it('长中英文名称保留完整数据，图形标签单行省略，完整 tooltip 可核对', () => {
    charts.current = [];
    const name = 'HTTP POST /支付订单/' + 'long-operation-'.repeat(30);
    render(<TraceWaterfall spans={[{ ...span, name }]} logs={[]} selectedId="" onSelect={vi.fn()} onLogSelect={vi.fn()} />);
    const option = charts.current[charts.current.length - 1].option;
    const series = option.series as { data: unknown[] }[];
    const tooltip = option.tooltip as { formatter: (event: unknown) => string };
    expect(tooltip.formatter({ data: series[0].data[0] })).toContain(name);
  });

  it('缩放改变实际时间视窗，日志与事件提示不计算不存在的耗时，点击携带正确记录', () => {
    charts.current = [];
    const log = { ts: span.ts, span_id: 's', body: '<log>' };
    const onLogSelect = vi.fn();
    const onEventSelect = vi.fn();
    render(<TraceWaterfall spans={[span]} logs={[log]} selectedId="s" onSelect={vi.fn()} onLogSelect={onLogSelect} onEventSelect={onEventSelect} />);
    fireEvent.click(screen.getByRole('button', { name: /^放大$/ }));
    const main = charts.current[charts.current.length - 1];
    expect(main.option.xAxis).toMatchObject({ min: 250, max: 750 });
    const series = main.option.series as { data: unknown[] }[];
    const tooltip = main.option.tooltip as { formatter: (event: unknown) => string };
    expect(tooltip.formatter({ data: series[1].data[0] })).toContain('<log>');
    expect(tooltip.formatter({ data: series[1].data[0] })).not.toContain('NaN');
    main.onEvents.click({ data: series[1].data[0] });
    main.onEvents.click({ data: series[2].data[0] });
    expect(onLogSelect).toHaveBeenCalledWith(log);
    expect(onEventSelect).toHaveBeenCalledWith(expect.objectContaining({ name: '<event>' }));
    act(() => charts.current[charts.current.length - 2].onEvents.datazoom({ start: 10, end: 20 }));
    expect(charts.current[charts.current.length - 1].option.xAxis).toMatchObject({ min: 100, max: 200 });
  });

  it('大链路仅绘制有界可视行，未关联日志有独立入口', () => {
    charts.current = [];
    const spans = Array.from({ length: 10000 }, (_, i) => ({ ...span, span_id: String(i), events: [] }));
    render(<TraceWaterfall spans={spans} logs={[{ ts: span.ts, span_id: 'missing', body: 'orphan' }]} selectedId="0" onSelect={vi.fn()} onLogSelect={vi.fn()} />);
    expect((charts.current[charts.current.length - 1].option.series as { data: unknown[] }[])[0].data.length).toBeLessThanOrEqual(18);
    expect(screen.getByRole('slider', { name: '可见链路行' })).toBeTruthy();
    fireEvent.change(screen.getByRole('slider', { name: '可见链路行' }), { target: { value: '9983' } });
    expect(screen.getByRole('button', { name: '未关联日志' })).toBeTruthy();
  });
});
