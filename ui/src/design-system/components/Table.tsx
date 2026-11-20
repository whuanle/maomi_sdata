import { useMemo, useState, type HTMLAttributes, type ReactNode } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/utils/cn';
import { Empty, SpinArea } from './feedback';
import { Select } from './Select';

/**
 * 表格列定义：刻意沿用 antd Table 的列 API 形状（title/dataIndex/render/key/width/align/ellipsis），
 * 页面从 antd 迁移时列配置基本零改动；新增 mono 标记（等宽数据列，HyperDX 风格）。
 */
export interface TableColumn<T> {
  key?: string;
  title: ReactNode;
  /** 取值路径，支持点号（如 attributes.http.method）；与 render 二选一或组合 */
  dataIndex?: string;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  render?: (value: any, record: T, index: number) => ReactNode;
  width?: number | string;
  align?: 'left' | 'center' | 'right';
  ellipsis?: boolean;
  /** 等宽字体数据列（时间戳/ID/数值等 HyperDX 观感） */
  mono?: boolean;
  hidden?: boolean;
}

export interface TablePaginationConfig {
  /** 服务端受控分页时传入 onChange；缺省 onChange 则为客户端分页并采用给定 pageSize */
  current?: number;
  pageSize?: number;
  total?: number;
  onChange?: (page: number, pageSize: number) => void;
  showSizeChanger?: boolean;
  pageSizeOptions?: number[];
}

export interface DataTableProps<T> {
  columns: TableColumn<T>[];
  dataSource?: T[];
  rowKey?: (keyof T & string) | ((record: T, index: number) => string);
  loading?: boolean;
  pagination?: false | TablePaginationConfig;
  emptyText?: ReactNode;
  rowClassName?: (record: T, index: number) => string;
  onRow?: (record: T, index: number) => HTMLAttributes<HTMLTableRowElement>;
  /** 横向滚动最小宽度（数字 px 或 CSS 值） */
  scrollX?: number | string;
  className?: string;
  stickyHeader?: boolean;
}

const DEFAULT_PAGE_SIZE = 10;
const CLIENT_PAGE_SIZE_OPTIONS = [10, 20, 50, 100];

/** 数据表（v2：原生 table + sticky 表头 + 分页，替代 antd Table）. */
export function DataTable<T>({
  columns,
  dataSource,
  rowKey,
  loading,
  pagination,
  emptyText,
  rowClassName,
  onRow,
  scrollX,
  className,
  stickyHeader = true,
}: DataTableProps<T>) {
  const { t } = useTranslation();
  const rows = dataSource ?? [];

  // 三种分页模式：false=关；对象带 onChange=服务端受控；其余（undefined 或对象缺 onChange）=客户端分页
  const clientPaged = pagination === undefined || (pagination !== false && !pagination.onChange);
  const serverPage = pagination === false || clientPaged ? undefined : pagination;
  const initialClientPageSize = (pagination !== undefined && pagination !== false ? pagination.pageSize : undefined) ?? DEFAULT_PAGE_SIZE;
  const clientSizeChanger = pagination === undefined ? true : (pagination !== false && pagination.showSizeChanger) ?? false;

  // 客户端分页内部状态（初始值来自 pagination 配置的 pageSize）
  const [clientPage, setClientPage] = useState(1);
  const [clientPageSizeState, setClientPageSize] = useState(initialClientPageSize);
  const clientTotal = rows.length;
  const clientPageCount = Math.max(1, Math.ceil(clientTotal / clientPageSizeState));
  const safeClientPage = Math.min(clientPage, clientPageCount);

  const view = useMemo(() => {
    if (serverPage || pagination === false) return rows;
    const start = (safeClientPage - 1) * clientPageSizeState;
    return rows.slice(start, start + clientPageSizeState);
  }, [rows, serverPage, pagination, safeClientPage, clientPageSizeState]);

  const visibleColumns = columns.filter((column) => !column.hidden);

  const keyOf = (record: T, index: number): string => {
    if (typeof rowKey === 'function') return rowKey(record, index);
    if (rowKey) return String(record[rowKey] ?? index);
    return String(index);
  };

  const readValue = (record: T, dataIndex?: string) => {
    if (!dataIndex) return undefined;
    return dataIndex.split('.').reduce<unknown>((acc, part) => {
      if (acc && typeof acc === 'object') return (acc as Record<string, unknown>)[part];
      return undefined;
    }, record);
  };

  // 分页参数归一
  const page = serverPage ? serverPage.current ?? 1 : safeClientPage;
  const pageSize = serverPage ? serverPage.pageSize ?? DEFAULT_PAGE_SIZE : clientPageSizeState;
  const total = serverPage ? serverPage.total ?? clientTotal : clientTotal;
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const showSizeChanger = serverPage ? serverPage.showSizeChanger ?? false : clientSizeChanger;
  const pageSizeOptions = serverPage?.pageSizeOptions ?? CLIENT_PAGE_SIZE_OPTIONS;

  const handlePageChange = (next: number) => {
    if (serverPage) {
      serverPage.onChange?.(next, pageSize);
    } else {
      setClientPage(next);
    }
  };

  const handlePageSizeChange = (next: number) => {
    if (serverPage) {
      serverPage.onChange?.(1, next);
    } else {
      setClientPageSize(next);
      setClientPage(1);
    }
  };

  return (
    <div className={cn('relative flex h-full min-h-0 flex-col', className)}>
      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full border-separate border-spacing-0 text-sm" style={{ minWidth: scrollX }}>
          <thead>
            <tr>
              {visibleColumns.map((column, index) => (
                <th
                  key={column.key ?? column.dataIndex ?? index}
                  scope="col"
                  style={{ width: column.width }}
                  className={cn(
                    'border-b border-border bg-surface-2 px-3 py-2 text-xs font-medium whitespace-nowrap text-subtle',
                    stickyHeader && 'sticky top-0 z-[1]',
                    column.align === 'right' && 'text-right',
                    column.align === 'center' && 'text-center',
                    !column.align && 'text-left',
                  )}
                >
                  {column.title}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {view.map((record, rowIndex) => (
              <tr
                key={keyOf(record, rowIndex)}
                className={cn(
                  'transition-colors hover:bg-surface-2/60',
                  rowClassName?.(record, rowIndex),
                )}
                {...onRow?.(record, rowIndex)}
              >
                {visibleColumns.map((column, colIndex) => {
                  const raw = readValue(record, column.dataIndex);
                  const content = column.render ? column.render(raw, record, rowIndex) : renderFallback(raw);
                  return (
                    <td
                      key={column.key ?? column.dataIndex ?? colIndex}
                      style={{ width: column.width }}
                      className={cn(
                        'border-b border-border/60 px-3 py-1.5 align-middle text-fg',
                        column.ellipsis && 'max-w-0 truncate',
                        column.mono && 'font-mono text-[13px]',
                        column.align === 'right' && 'text-right',
                        column.align === 'center' && 'text-center',
                      )}
                    >
                      {content}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
        {!loading && view.length === 0 && <Empty description={emptyText ?? t('common.empty')} />}
      </div>

      {loading && (
        <div className="absolute inset-0 z-[2] grid place-items-center bg-[var(--c-surface)]/60">
          <SpinArea />
        </div>
      )}

      {(serverPage !== undefined || (clientPaged && total > 0)) && (
        <div className="flex shrink-0 items-center justify-between gap-3 px-1 py-2 text-xs text-subtle">
          <span>{t('common.total', { count: total })}</span>
          <div className="flex items-center gap-2">
            {showSizeChanger && (
              <Select
                aria-label="page size"
                className="h-7 w-24 text-xs"
                value={String(pageSize)}
                onValueChange={(value) => handlePageSizeChange(Number(value))}
                options={pageSizeOptions.map((size) => ({ label: `${size} / 页`, value: String(size) }))}
              />
            )}
            <div className="flex items-center gap-1">
              <button
                type="button"
                aria-label="previous page"
                disabled={page <= 1}
                onClick={() => handlePageChange(page - 1)}
                className="flex size-6 cursor-pointer items-center justify-center rounded-sm text-subtle transition-colors hover:bg-surface-2 hover:text-fg disabled:pointer-events-none disabled:opacity-40"
              >
                <ChevronLeft className="size-3.5" aria-hidden />
              </button>
              <span className="font-mono">
                {page} / {pageCount}
              </span>
              <button
                type="button"
                aria-label="next page"
                disabled={page >= pageCount}
                onClick={() => handlePageChange(page + 1)}
                className="flex size-6 cursor-pointer items-center justify-center rounded-sm text-subtle transition-colors hover:bg-surface-2 hover:text-fg disabled:pointer-events-none disabled:opacity-40"
              >
                <ChevronRight className="size-3.5" aria-hidden />
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/** 未提供 render 时的默认单元格内容（null/undefined → '-'，布尔 → 文案）. */
function renderFallback(value: unknown): ReactNode {
  if (value === null || value === undefined || value === '') return '-';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
