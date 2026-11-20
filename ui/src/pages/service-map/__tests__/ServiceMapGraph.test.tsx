import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@/i18n';
import type { EChartsOption } from 'echarts';
import { ServiceMapGraph } from '../ServiceMapGraph';
import type { MapMetric } from '../serviceMapModel';

const captured = vi.hoisted(() => ({ option: {} as EChartsOption }));
vi.mock('@/design-system/Chart', () => ({
  Chart: ({ option, onEvents }: { option: EChartsOption; onEvents: { click: (event: unknown) => void } }) => {
    captured.option = option;
    return <button onClick={() => onEvents.click({ dataType: 'edge', data: { metric: { source: '<api>', target: 'db' } } })}>select edge</button>;
  },
}));
const node = (source: string): MapMetric => ({ source, target: '', calls: 4, traces: 2, errors: 1, duration: 500, firstSeen: '', lastSeen: '' });
describe('ServiceMapGraph', () => {
  it('uses non-HTML tooltips, directional links and bounded labels', () => {
    const select = vi.fn();
    render(<ServiceMapGraph nodes={[node('<api>'), node('db')]} edges={[{ ...node('<api>'), target: 'db' }]} color="errorRate" focus={null} onSelect={select} />);
    expect(captured.option.tooltip).toMatchObject({ renderMode: 'richText' });
    const series = (captured.option.series as { layout: string; edgeSymbol: string[]; label: { overflow: string }; data: { id: string; x: number }[] }[])[0];
    expect(series.layout).toBe('none');
    expect(series.edgeSymbol).toEqual(['none', 'arrow']);
    expect(series.label.overflow).toBe('truncate');
    expect(series.data.find((item) => item.id === 'db')!.x).toBeGreaterThan(series.data.find((item) => item.id === '<api>')!.x);
    fireEvent.click(screen.getByRole('button', { name: 'select edge' }));
    expect(select).toHaveBeenCalledWith({ source: '<api>', target: 'db' });
  });
  it('focuses neighbors without dropping the selected service', () => {
    render(<ServiceMapGraph nodes={[node('api'), node('db'), node('isolated')]} edges={[{ ...node('api'), target: 'db' }]} color="calls" focus="api" onSelect={vi.fn()} />);
    const series = (captured.option.series as { data: { id: string }[] }[])[0];
    expect(series.data.map((item) => item.id).sort()).toEqual(['api', 'db']);
  });
});
