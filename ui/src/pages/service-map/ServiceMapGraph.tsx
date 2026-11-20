import { useMemo } from 'react';
import type { EChartsOption } from 'echarts';
import { useTranslation } from 'react-i18next';
import { Chart } from '@/design-system';
import { useCssTheme } from '@/design-system/theme';
import { formatDurationUs } from '@/utils/format';
import { layoutServices, type MapMetric } from './serviceMapModel';

export type MapColor = 'latency' | 'errorRate' | 'calls';
export function ServiceMapGraph({ nodes, edges, color, focus, onSelect }: {
  nodes: MapMetric[]; edges: MapMetric[]; color: MapColor; focus: string | null;
  onSelect: (selection: { source: string; target?: string }) => void;
}) {
  const { t } = useTranslation();
  const theme = useCssTheme();
  const option = useMemo<EChartsOption>(() => {
    const neighbors = new Set([focus, ...edges.filter((edge) => edge.source === focus || edge.target === focus).flatMap((edge) => [edge.source, edge.target])]);
    const visible = focus === null ? nodes : nodes.filter((node) => neighbors.has(node.source));
    const names = new Set(visible.map((node) => node.source));
    const links = edges.filter((edge) => names.has(edge.source) && names.has(edge.target));
    const positions = new Map(layoutServices([...names], links).map((point) => [point.name, point]));
    const max = Math.max(1, ...nodes.map((node) => node.calls));
    const metricColor = (metric: MapMetric) => {
      if (color === 'calls') return theme.primary;
      const value = color === 'errorRate' ? metric.errors / Math.max(1, metric.calls) : metric.duration;
      return value <= 0 ? theme.textFaint : value < (color === 'errorRate' ? 0.05 : 100000) ? theme.success : value < (color === 'errorRate' ? 0.2 : 1000000) ? theme.warning : theme.error;
    };
    return {
      animation: false,
      tooltip: { renderMode: 'richText', confine: true, formatter: (raw: unknown) => {
        const metric = (raw as { data: { metric: MapMetric } }).data.metric;
        return [metric.target ? metric.source + ' → ' + metric.target : metric.source || t('serviceMap.unknown'),
          t('serviceMap.calls') + ': ' + metric.calls, t('serviceMap.traces') + ': ' + metric.traces,
          t('serviceMap.errorRate') + ': ' + (metric.errors / Math.max(1, metric.calls) * 100).toFixed(1) + '%',
          t('serviceMap.latency') + ': ' + formatDurationUs(metric.duration)].join('\n');
      } },
      series: [{
        type: 'graph', layout: 'none', roam: true, draggable: true,
        edgeSymbol: ['none', 'arrow'], edgeSymbolSize: [0, 9],
        label: { show: true, position: 'bottom', color: theme.text, width: 150, overflow: 'truncate', formatter: (item: { name: string }) => item.name || t('serviceMap.unknown') },
        lineStyle: { color: theme.border, curveness: 0.15, opacity: 0.8 },
        emphasis: { focus: 'adjacency' },
        data: visible.map((metric) => ({
          id: metric.source, name: metric.source, ...positions.get(metric.source), metric,
          symbolSize: 28 + Math.sqrt(metric.calls / max) * 36,
          itemStyle: { color: metricColor(metric), borderColor: theme.surface, borderWidth: 2 },
        })),
        links: links.map((metric) => ({ source: metric.source, target: metric.target, metric,
          lineStyle: { color: metricColor(metric), width: 1 + Math.sqrt(metric.calls / max) * 5, curveness: 0.15 } })),
      }],
    };
  }, [nodes, edges, color, focus, t, theme]);
  return <Chart option={option} minHeight={0} onEvents={{ click: (event) => {
    const metric = (event.data as { metric?: MapMetric })?.metric;
    if (metric) onSelect({ source: metric.source, ...(event.dataType === 'edge' ? { target: metric.target } : {}) });
  } }} />;
}
