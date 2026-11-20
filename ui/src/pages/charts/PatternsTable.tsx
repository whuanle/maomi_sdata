import { useMemo, useState } from 'react';
import { Code, Copy, Download, WrapText } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, Chart, DataTable, Empty, Tag, toast, type TableColumn, type TimeRange } from '@/design-system';
import { useCssTheme } from '@/design-system/theme';
import { formatDateTime } from '@/utils/format';
import { cn } from '@/utils/cn';
import { PATTERN_LIMIT, patternRows, patternTotal, patternsCsv, type PatternRow } from './patterns';
import type { AnalyticsQueryResponse } from '@/api/types';

export function PatternsTable({ result, loading, range, onOpen, onSql }: {
  result: AnalyticsQueryResponse | null;
  loading: boolean;
  range: TimeRange;
  onOpen?: (sample: Record<string, unknown>) => void;
  onSql?: () => void;
}) {
  const { t } = useTranslation();
  const theme = useCssTheme();
  const [wrap, setWrap] = useState(true);
  const rows = useMemo(() => patternRows(result), [result]);
  const total = patternTotal(result);
  const download = () => {
    const url = URL.createObjectURL(new Blob(['\uFEFF', patternsCsv(rows)], { type: 'text/csv;charset=utf-8' }));
    const anchor = document.createElement('a');
    anchor.href = url; anchor.download = 'patterns.csv'; anchor.click();
    URL.revokeObjectURL(url);
  };
  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); toast.success(t('charts.copied')); }
    catch { toast.error(t('search.copyFailed')); }
  };
  const columns: TableColumn<PatternRow>[] = [
    { key: 'trend', title: t('charts.patterns.trend'), width: 160, render: (_, row) => <div className="h-12 w-36" aria-label={t('charts.patterns.trend')}>
      <Chart minHeight={48} option={{
        animation: false, grid: { top: 4, bottom: 4, left: 2, right: 2 },
        xAxis: { type: 'category', data: row.trend.map((_, i) => formatDateTime(new Date(Date.parse(range.from) + (Date.parse(range.to) - Date.parse(range.from)) * i / row.trend.length).toISOString())), show: false },
        yAxis: { type: 'value', show: false, min: 0 },
        tooltip: { trigger: 'axis' as const },
        series: [{ type: 'bar', data: row.trend, itemStyle: { color: theme.primary } }],
      }} />
    </div> },
    { key: 'count', title: t('charts.patterns.count'), width: 100, mono: true, render: (_, row) => row.count.toLocaleString() },
    { key: 'level', title: t('charts.patterns.level'), width: 100, render: (_, row) => <Tag tone={['error', 'fatal'].includes(row.level) ? 'error' : ['warn', 'warning'].includes(row.level) ? 'warning' : 'default'}>{row.level || '—'}</Tag> },
    { key: 'pattern', title: t('charts.patterns.pattern'), render: (_, row) => <div className="group flex min-w-0 items-start gap-2">
      <button type="button" disabled={!row.sample || !onOpen} title={t('charts.patterns.representative')} onClick={() => row.sample && onOpen?.(row.sample)} className={cn('min-w-0 flex-1 cursor-pointer text-left font-mono text-xs leading-6 hover:text-primary disabled:cursor-default', wrap ? 'whitespace-pre-wrap break-words' : 'max-w-[70vw] overflow-hidden text-ellipsis whitespace-nowrap')}>
        {row.pattern.split(/(<\*>)/).map((part, i) => part === '<*>' ? <span key={i} className="text-primary">{part}</span> : part)}
      </button>
      <Button size="sm" variant="link" icon={<Copy className="size-3" />} onClick={() => void copy(row.pattern)}>{t('charts.patterns.copy')}</Button>
    </div> },
  ];
  return <section aria-label={t('charts.tabPatterns')} className="min-w-0">
    <div className="flex flex-wrap items-center justify-end gap-1 border-b border-border py-1">
      {onSql && <Button size="sm" variant="ghost" icon={<Code className="size-3.5" />} onClick={onSql} disabled={!result}>{t('charts.previewSql')}</Button>}
      <Button size="sm" variant="ghost" icon={<WrapText className="size-3.5" />} aria-pressed={wrap} onClick={() => setWrap(!wrap)}>{t(wrap ? 'charts.patterns.unwrap' : 'charts.patterns.wrap')}</Button>
      <Button size="sm" variant="ghost" icon={<Download className="size-3.5" />} onClick={download} disabled={!rows.length}>{t('charts.patterns.export')}</Button>
    </div>
    <p className="py-2 text-xs text-muted">{t('charts.patterns.scope')}</p>
    {total > PATTERN_LIMIT && <p role="status" className="pb-2 text-xs text-warning">{t('charts.patterns.limit', { count: PATTERN_LIMIT, total })}</p>}
    <DataTable columns={columns} dataSource={rows} rowKey="key" loading={loading} pagination={false} emptyText={<Empty description={t('charts.noDataHint')} className="py-8" />} />
  </section>;
}
