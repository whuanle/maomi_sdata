import { useTranslation } from 'react-i18next';
import { Chart, Empty, Spin } from '@/design-system';
import { useCssTheme } from '@/design-system/theme';
import type { NumberDisplaySettings, NumberFormat, NumberResult, NumberTrendQuery } from './numberTypes';
import { NUMBER_COLOR_CLASSES, normalizeNumberSettings } from './numberTypes';
import { formatNumberValue, numberColor } from './numberFormat';
import { cn } from '@/utils/cn';
import { useNumberTrend } from './useNumberTrend';
import { numberTrendPoints } from './numberTrend';

interface NumberItem { id: string; alias: string; format?: NumberFormat }

/** 探索器与仪表盘共用；趋势独立失败，不影响主值. */
export function NumberChart({ result, settings: savedSettings, items = [], trendQuery, range, loading = false, compact = false, executionKey }: {
  result?: NumberResult | null; settings?: NumberDisplaySettings; items?: NumberItem[];
  trendQuery?: NumberTrendQuery; range?: { from: string; to: string }; loading?: boolean; compact?: boolean; executionKey?: unknown;
}) {
  const { t } = useTranslation();
  const theme = useCssTheme();
  const settings = normalizeNumberSettings(savedSettings);
  const enabled = settings.background !== 'none';
  const trend = useNumberTrend(enabled, trendQuery, range, executionKey);
  const container = compact ? 'h-full min-h-24' : 'min-h-[360px]';
  if (loading) return <div className={cn('flex items-center justify-center', container)} role="status"><Spin /><span className="sr-only">{t('common.loading')}</span></div>;
  if (!result) return <Empty description={t('charts.noDataHint')} className={cn('flex items-center justify-center', container)} />;
  const valueIndex = result.columns.indexOf('value');
  const idIndex = result.columns.indexOf('__series_id');
  const nameIndex = result.columns.indexOf('series');
  if (valueIndex < 0 && result.rows.length) return <p role="alert" className="p-6 text-center text-warning">{t('numberChart.valueColumnRequired')}</p>;
  const rows = result.rows.length ? result.rows : [[null]];
  return <div className={cn('flex flex-col', container)}>
    <div className="flex flex-1 flex-wrap items-stretch justify-center gap-4">
      {rows.map((row, index) => {
        const id = idIndex >= 0 ? String(row[idIndex]) : result.rows.length === 1 ? items[0]?.id : undefined;
        const item = items.find((item) => item.id === id);
        const label = nameIndex >= 0 ? String(row[nameIndex] ?? '') : item?.alias ?? '';
        const value = row[valueIndex];
        const color = numberColor(value, settings);
        const points = numberTrendPoints(trend.data, id, label, rows.length === 1, range);
        const backgroundColor = settings.backgroundColor ?? settings.color;
        const stroke = backgroundColor === 'default' ? theme.primary : theme[backgroundColor];
        return <div key={id ?? index} className={cn('relative flex min-w-0 flex-1 basis-64 flex-col items-center justify-center overflow-hidden rounded px-4 py-6', NUMBER_COLOR_CLASSES[color])}>
          {enabled && points.length > 0 && range && <div className="pointer-events-none absolute inset-0 opacity-20" aria-hidden><Chart minHeight={0} option={{
            animation: false, grid: { left: 0, right: 0, top: 16, bottom: 0 },
            xAxis: { type: 'time', show: false, min: Date.parse(range.from), max: Date.parse(range.to) },
            yAxis: { type: 'value', show: false, scale: true },
            series: [{ type: settings.background === 'bar' ? 'bar' : 'line', showSymbol: false, connectNulls: false,
              areaStyle: settings.background === 'area' ? {} : undefined, itemStyle: { color: stroke }, data: points }],
          }} /></div>}
          <div className={cn('relative max-w-full break-all text-center font-mono leading-tight tabular-nums', compact ? 'text-3xl xl:text-4xl' : 'text-5xl md:text-7xl')} title={value == null ? undefined : String(value)}>{formatNumberValue(value, item?.format ?? settings.format)}</div>
          {(rows.length > 1 || item?.alias) && <div className="relative mt-3 max-w-full break-words text-center font-mono text-xs text-muted">{label}</div>}
        </div>;
      })}
    </div>
    {result.rows.length === 0 && <p className="pb-3 text-center text-xs text-muted">{t('numberChart.noData')}</p>}
    {enabled && (!trendQuery || trend.error) && <p role="status" className="pb-3 text-center text-xs text-muted">{t(trend.error ? 'numberChart.trendError' : 'numberChart.trendUnavailable')}</p>}
    {enabled && trend.data?.truncated && <p role="status" className="pb-3 text-center text-xs text-warning">{t('numberChart.trendTruncated')}</p>}
    {enabled && trendQuery && trend.data && !trend.data.rows.length && <p role="status" className="pb-3 text-center text-xs text-muted">{t('numberChart.trendNoData')}</p>}
  </div>;
}
