import { useMemo, useState } from 'react';
import { ArrowDownUp, Download } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button, DataTable, Empty } from '@/design-system';
import type { TableDisplaySettings, TableColumnIdentity, TableSort } from '@/api/chartTypes';
import { downloadCsv } from '@/pages/search/csv';

interface Result { columns: string[]; rows: unknown[][] }
const colors = { default: 'text-fg', primary: 'text-primary', success: 'text-success', warning: 'text-warning' };

export function formatTableValue(value: unknown, settings: TableDisplaySettings = {}): string {
  if (value == null) return '—';
  if (typeof value !== 'number') return typeof value === 'object' ? JSON.stringify(value) : String(value);
  if (!Number.isFinite(value)) return '—';
  const decimals = Math.max(0, Math.min(8, settings.decimals ?? 2));
  if (settings.format === 'bytes') {
    const unit = value === 0 ? 0 : Math.min(4, Math.max(0, Math.floor(Math.log(Math.abs(value)) / Math.log(1024))));
    return `${(value / 1024 ** unit).toLocaleString(undefined, { maximumFractionDigits: decimals })} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}`;
  }
  return value.toLocaleString(undefined, { maximumFractionDigits: decimals }) + (settings.format === 'percent' ? '%' : '');
}

/** 按位置读取列值，避免带点号的 SQL 别名被解释为对象路径. */
export function ChartTable({ result, settings = {}, loading = false, onRowClick, columns, querySort, onSort }: {
  columns?: TableColumnIdentity[]; querySort?: TableSort[]; onSort?: (sort: TableSort) => void;
  result: Result | null; settings?: TableDisplaySettings; loading?: boolean; onRowClick?: (row: unknown[]) => void;
}) {
  const { t } = useTranslation();
  const [sort, setSort] = useState<{ index: number; desc: boolean; schema: string } | null>(null);
  const schema = result?.columns.join('\0') ?? '';
  const activeSort = !onSort && sort && sort.schema === schema ? sort : null;
  const rows = useMemo(() => {
    const items = (result?.rows ?? []).map((cells, index) => ({ cells, index }));
    if (activeSort) items.sort((a, b) => {
      const left = a.cells[activeSort.index], right = b.cells[activeSort.index];
      if (left == null) return right == null ? 0 : 1;
      if (right == null) return -1;
      const value = typeof left === 'number' && typeof right === 'number' ? left - right : String(left).localeCompare(String(right));
      return activeSort.desc ? -value : value;
    });
    return items;
  }, [result, activeSort]);
  return <section className="flex h-full min-h-0 min-w-0 flex-col [&_td]:py-1.5 [&_table]:text-xs" aria-label={t('charts.tabTable')}>
    <div className="flex items-center justify-between gap-2 border-y border-border py-1">
      <span className="text-xs text-muted">{t(onSort ? 'charts.tableQuerySortHint' : 'charts.tableSortHint')}</span>
      <Button size="sm" variant="ghost" disabled={!rows.length} icon={<Download className="size-3.5" />} onClick={() => downloadCsv(result?.columns ?? [], rows.map((row) => row.cells), 'chart-results.csv')}>{t('charts.tableDownload')}</Button>
    </div>
    <DataTable stickyHeader={settings.stickyHeader !== false} rowClassName={(_, index) => [settings.striped && index % 2 === 1 ? 'bg-surface-2/50' : '', onRowClick && settings.rowClick !== false ? 'cursor-pointer' : ''].join(' ')} columns={(result?.columns ?? []).map((name, index) => ({
      key: String(index), mono: true, align: columns?.find((column) => column.name === name)?.kind === 'metric' || result?.rows.some((row) => typeof row[index] === 'number') ? 'right' as const : 'left' as const,
      title: <button type="button" disabled={loading} className="flex w-full cursor-pointer items-center justify-between gap-2 whitespace-nowrap disabled:cursor-wait" onClick={() => { const identity = columns?.find((column) => column.name === name); if (onSort && identity) onSort({ columnId: identity.id, direction: querySort?.[0]?.columnId === identity.id && querySort[0].direction === 'desc' ? 'asc' : 'desc' }); else setSort((current) => ({ index, schema, desc: current?.schema === schema && current.index === index ? !current.desc : true })); }}>{name}<ArrowDownUp className="size-3" />{onSort && querySort?.[0]?.columnId === columns?.find((column) => column.name === name)?.id ? (querySort?.[0]?.direction === 'desc' ? '↓' : '↑') : activeSort?.index === index ? (activeSort.desc ? '↓' : '↑') : ''}</button>,
      render: (_: unknown, row: { cells: unknown[] }) => {
        const columnSettings = { ...settings, ...settings.columns?.[columns?.find((column) => column.name === name)?.id ?? name] };
        return <span className={typeof row.cells[index] === 'number' ? colors[columnSettings.color ?? 'default'] : 'text-fg'}>{formatTableValue(row.cells[index], columnSettings)}</span>;
      },
    }))} dataSource={rows} rowKey={(row) => String(row.index)} loading={loading} pagination={{ pageSize: 50, showSizeChanger: false }}
      onRow={onRowClick && settings.rowClick !== false ? (row) => ({ onClick: () => onRowClick(row.cells) }) : undefined}
      emptyText={<Empty description={t('charts.noDataHint')} className="py-8" />} />
  </section>;
}
