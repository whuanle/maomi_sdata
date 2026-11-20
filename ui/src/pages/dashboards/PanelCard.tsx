import { NumberChart } from '@/pages/charts/NumberChart';
import { PatternsTable } from '@/pages/charts/PatternsTable';
import { ChartTable } from '@/pages/charts/ChartTable';
import { panelResult, chartTableResult } from '@/pages/charts/chartResult';
import { buildChartOption } from '@/pages/charts/chartOption';
import { barData, buildBarOption } from '@/pages/charts/barDisplay';
import { panelVisualizationUnsupported, resolvePanelTrend, resolvePanelVisualization } from '@/pages/charts/panelDefinition';
import { buildDistributionOption } from '@/pages/charts/heatmap';
import type { ChartType } from '@/pages/charts/buildChartSql';
import { useCssTheme } from '@/design-system/theme';
import { useTranslation } from 'react-i18next';
import { Spin, Tag, Chart, TimeSeriesChart, formatChartValue, Markdown } from '@/design-system';
import type { PanelConfig, PanelData, PanelSeries } from '@/api/types';

/** 12 列栅格宽度 → Tailwind col-span（静态类，禁动态拼接）. */
const GRID_W_CLASS: Record<number, string> = {
  1: 'col-span-1',
  2: 'col-span-2',
  3: 'col-span-3',
  4: 'col-span-4',
  5: 'col-span-5',
  6: 'col-span-6',
  7: 'col-span-7',
  8: 'col-span-8',
  9: 'col-span-9',
  10: 'col-span-10',
  11: 'col-span-11',
  12: 'col-span-12',
};

/** 高度档位 → 固定高度类. */
const H_CLASS: Record<number, string> = {
  1: 'h-[120px]',
  2: 'h-[220px]',
  3: 'h-[320px]',
  4: 'h-[420px]',
  5: 'h-[520px]',
  6: 'h-[620px]',
  7: 'h-[720px]',
  8: 'h-[820px]',
};

/** 面板栅格宽度类. */
export function panelSpanClass(w?: number): string {
  return GRID_W_CLASS[Math.min(12, Math.max(1, w ?? 4))] ?? 'col-span-4';
}

/** 面板高度类. */
export function panelHeightClass(h?: number): string {
  return H_CLASS[Math.min(8, Math.max(1, h ?? 4))] ?? 'h-[420px]';
}

/** 面板数值按单位格式化. */
export const formatPanelValue = formatChartValue;

/** 序列展示名：labels JSON → `name {k=v, …}`（无标签回退 name）. */
export function seriesDisplayName(series: PanelSeries): string {
  let labels: Record<string, unknown> = {};
  try {
    labels = JSON.parse(series.labels || '{}') as Record<string, unknown>;
  } catch {
    labels = {};
  }

  const entries = Object.entries(labels);
  if (entries.length === 0) {
    return series.name;
  }

  const label = entries
    .slice(0, 2)
    .map(([key, value]) => `${key}=${String(value)}`)
    .join(', ');
  const more = entries.length > 2 ? `, +${entries.length - 2}` : '';
  return `${series.name} {${label}${more}}`;
}

/**
 * 面板主体渲染（预览与查看共用）：series → 图表/stat，columns+rows → 表格.
 */
export function PanelCardBody({ config, data, loading }: { config: PanelConfig; data?: PanelData | null; loading?: boolean }) {
  const { t } = useTranslation();
  const theme = useCssTheme();
  const executionKey = data;
  if (data && config.query?.kind === 'custom') {
    const nodes = config.query.custom?.nodes ?? [];
    const identityIndex = data.columns.indexOf('__series_id');
    data = { ...data, rows: data.rows.map(row => {
      const ref = String(row[identityIndex] ?? '').split(':')[0];
      const node = nodes.find(item => item.ref === ref);
      if (!node?.alias) return row;
      return row.map((value, index) => ['series', 'category'].includes(data!.columns[index]) && typeof value === 'string'
        ? node.alias + value.slice(ref.length) : value);
    }) };
  }

  if (data && config.query?.kind === 'custom' && ['bar', 'pie'].includes(config.type)) {
    const indices = data.columns.map((_, index) => index).filter(index => !['ts_bucket', 'series'].includes(data!.columns[index]));
    data = { ...data, columns: indices.map(index => data!.columns[index]), rows: data.rows.map(row => indices.map(index => row[index])) };
  }
  const series = data?.series ?? [];
  if (config.type === 'markdown') return <Markdown content={config.markdown ?? ''} className="h-full overflow-auto" />;
  if (loading && !data) {
    return (
      <div className="flex h-full items-center justify-center">
        <Spin />
      </div>
    );
  }

  if (data?.error) {
    return (
      <div className="flex h-full flex-col justify-center gap-1 overflow-auto">
        <Tag tone="error">{data.error}</Tag>
      </div>
    );
  }

  const visual = resolvePanelVisualization(config);
  if (panelVisualizationUnsupported(config)) return <p role="alert" className="text-warning">{t('charts.unsupportedPanelDefinition')}</p>;
  if (data && visual) data = panelResult(data, visual);
  const range = data?.from && data.to ? { from: data.from, to: data.to } : visual?.range;
  if (data && visual?.kind === 'number' && range) return <NumberChart compact executionKey={executionKey} result={data} settings={visual.numberSettings} range={range} trendQuery={resolvePanelTrend(config)} items={visual.outputs.map((item) => ({ ...item, format: visual.numberFormats?.[item.id] }))} />;
  if (data && visual?.kind === 'heatmap' && visual.heatmap && range) {
    const option = buildDistributionOption(data, range, visual.heatmap, config.showLegend !== false, {
      low: t('charts.heatmapLow'), high: t('charts.heatmapHigh'), count: t('charts.heatmapCount'),
      duration: t('charts.heatmapDuration'), value: t('charts.heatmapValue'), noData: t('charts.noDataHint'),
    });
    return option ? <Chart option={option} minHeight={0} /> : <p className="text-muted">{t('charts.noDataHint')}</p>;
  }
  if (data && visual?.kind === 'bar') {
    const metric = visual.outputs[0];
    const name = metric?.alias || t('charts.barTotal', { metric: metric?.label ?? 'value' });
    const format = metric?.format ?? visual.barSettings?.format;
    const option = buildBarOption(data, name, format ? { ...visual.barSettings, format } : undefined, theme.border, config.showLegend !== false);
    return option ? <Chart option={option} minHeight={0} /> : <p className="text-muted">{t(barData(data, name).invalid ? 'charts.barValueRequired' : 'charts.noDataHint')}</p>;
  }
  if (data && visual?.kind === 'pie' && range) {
    const settings = visual.pieSettings;
    const format = visual.outputs[0]?.format ?? settings?.format;
    const option = buildChartOption(data, 'pie', range, visual.outputs, config.showLegend !== false, 'line', theme.border, format ? { ...settings, format } : undefined);
    return option ? <Chart option={option} minHeight={0} /> : <p className="text-muted">{t('charts.pieNoPositive')}</p>;
  }
  if (data && visual && range && ['line', 'area', 'bar', 'heatmap'].includes(config.type)) {
    const option = buildChartOption(data, visual.kind as ChartType, range, visual.outputs, config.showLegend !== false, visual.display, theme.border);
    if (option && config.type === 'area' && Array.isArray(option.series)) option.series = option.series.map(item => ({ ...item, areaStyle: {} }));
    if (option) return <Chart option={option} minHeight={0} />;
  }

  if (series.length > 0 && ['line', 'area', 'bar'].includes(config.type)) {
    return (
      <div className="h-full min-h-[80px]">
        <TimeSeriesChart series={series} type={config.type as 'line' | 'area' | 'bar'} unit={config.unit} showLegend={config.showLegend} />
      </div>
    );
  }

  // 旧 SQL 返回 columns/rows；显式图表类型继续按首列分类轴绘制所有数值列.
  if (data && ['line', 'area', 'bar'].includes(config.type) && data.columns.length > 1) {
    const sqlData = data;
    const numeric = sqlData.columns.map((column, index) => ({ column, index }))
      .filter(({ index }) => index > 0 && sqlData.rows.some((row) => typeof row[index] === 'number'));
    if (numeric.length > 0) return <Chart minHeight={0} option={{
      tooltip: { trigger: 'axis', renderMode: 'richText', valueFormatter: (value) => formatChartValue(typeof value === 'number' ? value : null, config.unit) },
      legend: { show: config.showLegend !== false && numeric.length > 1, type: 'scroll' },
      grid: { left: 8, right: 16, top: 28, bottom: 8, containLabel: true },
      xAxis: { type: 'category', data: sqlData.rows.map((row) => String(row[0] ?? '')) },
      yAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatChartValue(value, config.unit) } },
      series: numeric.map(({ column, index }) => ({ name: column, type: config.type === 'bar' ? 'bar' : 'line',
        areaStyle: config.type === 'area' ? {} : undefined,
        data: sqlData.rows.map((row) => typeof row[index] === 'number' ? row[index] as number : null) })),
    }} />;
  }

  // stat：取第一条序列末值（或表格首行末列）大数展示
  if (config.type === 'stat') {
    const statValue = series.length > 0
      ? series[0].points.filter((point) => point.value != null).slice(-1)[0]?.value
      : (data?.rows?.[0]?.[(data.columns.length ?? 1) - 1] as number | undefined);
    return (
      <div className="flex h-full items-center justify-center">
        <span className="font-mono text-4xl font-bold text-fg">{formatPanelValue(statValue, config.unit)}</span>
      </div>
    );
  }

  if (data && visual?.kind === 'patterns') return <div className="h-full overflow-auto"><PatternsTable result={{ ...data, truncated: false }} loading={Boolean(loading)} range={data.from && data.to ? { from: data.from, to: data.to } : visual.range} /></div>;
  if (data && visual?.kind === 'search') return <ChartTable result={chartTableResult(data)} settings={{ decimals: 8 }} />;

  if (data && config.type === 'table' && visual?.tableSettings) return <ChartTable result={chartTableResult(data)} settings={visual.tableSettings} columns={visual.tableColumns} />;

  // 表格
  if (data && visual) data = chartTableResult(data);
  const columns = data?.columns ?? [];
  const rows = data?.rows ?? [];
  if (columns.length === 0) {
    return <div className="flex h-full items-center justify-center text-sm text-faint">{t('dashboard.noData')}</div>;
  }

  return (
    <div className="h-full overflow-auto">
      <table className="w-full border-collapse text-xs">
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column} className="sticky top-0 border-b border-border bg-surface px-1.5 py-1.5 text-left font-medium text-subtle">{column}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.slice(0, 50).map((row, rowIndex) => (
            <tr key={rowIndex}>
              {row.map((cell, cellIndex) => (
                <td key={cellIndex} className="border-b border-border/60 px-1.5 py-1.5 font-mono text-fg">
                  {cell === null || cell === undefined ? '-' : String(cell)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > 50 && <div className="px-1.5 py-1 text-[11px] text-faint">{t('dashboard.rowsTruncated', { count: rows.length })}</div>}
    </div>
  );
}

/**
 * 面板卡片：标题 + 耗时 + 主体（查看态与预览共用渲染）.
 */
export function PanelCard({ config, data, loading, actions, draggable = false }: { config: PanelConfig; data?: PanelData | null; loading?: boolean; actions?: React.ReactNode; draggable?: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex h-full flex-col rounded-lg border border-border bg-surface">
      <div className={`flex shrink-0 flex-wrap items-center justify-between gap-2 border-b border-border px-3 pt-2 pb-1.5 ${draggable ? 'dashboard-drag-handle cursor-move' : ''}`}>
        <span className="truncate text-sm font-medium text-fg">{config.title || config.id}</span>
        <span className="flex items-center gap-1.5">
          {config.type !== 'markdown' && data && !data.error && <span className="text-[11px] text-faint">{data.elapsedMs}ms</span>}
          {data?.truncated && <Tag tone="warning">{t('charts.resultTruncated')}</Tag>}
          {actions}
        </span>
      </div>
      <div className="min-h-0 flex-1 px-3 py-2">
        <PanelCardBody config={config} data={data} loading={loading} />
      </div>
    </div>
  );
}
