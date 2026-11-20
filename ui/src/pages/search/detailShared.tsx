import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronRight, Copy, RefreshCw, Search } from 'lucide-react';
import { Input, Tooltip } from '@/design-system';
import { cn } from '@/utils/cn';
import { formatDateTimeMs } from '@/utils/format';
import { SEVERITY_TEXT_CLS } from './detailFormat';

/** 明细行对象（列名 → 单元格值）别名，语义标注. */
export type DetailRow = Record<string, unknown>;

/** 分区标题（HyperDX 式：可折叠小标题 + 悬浮复制整区按钮，默认展开）. */
export function Section({ title, raw, onCopy, children }: { title: string; raw?: string; onCopy?: (text: string) => void; children: React.ReactNode }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(true);
  return (
    <section className="group/section flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <button
          type="button"
          className="flex cursor-pointer items-center gap-1 text-xs font-medium text-subtle hover:text-fg"
          aria-expanded={open}
          onClick={() => setOpen((prev) => !prev)}
        >
          {open ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
          {title}
        </button>
        {raw !== undefined && raw.length > 0 && onCopy !== undefined && (
          <Tooltip content={t('search.copySection')}>
            <button
              type="button"
              aria-label={t('search.copySection')}
              className="cursor-pointer p-0.5 text-faint opacity-0 transition-opacity group-hover/section:opacity-100 hover:text-fg"
              onClick={() => onCopy(raw)}
            >
              <Copy className="size-3.5" aria-hidden />
            </button>
          </Tooltip>
        )}
      </div>
      {open && children}
    </section>
  );
}

/** 属性搜索框（按键或值过滤）. */
export function SearchBox({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const { t } = useTranslation();
  return (
    <Input
      size="sm"
      value={value}
      placeholder={t('search.searchProperties')}
      onChange={(event) => onChange(event.target.value)}
      prefix={<Search className="size-3.5" aria-hidden />}
    />
  );
}

/** 错误提示条（含重试）. */
export function ErrorHint({ message, onRetry }: { message: string; onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center justify-between gap-2 rounded-md border border-error/40 bg-error-soft px-2.5 py-1.5 text-xs text-error">
      <span className="min-w-0 break-all">{message}</span>
      <button type="button" className="flex shrink-0 cursor-pointer items-center gap-1 hover:underline" onClick={onRetry}>
        <RefreshCw className="size-3" aria-hidden />
        {t('search.retry')}
      </button>
    </div>
  );
}

/** 紧凑日志列表表头（时间/级别/服务/内容，与 MiniLogList 行列对齐）. */
function MiniLogHeader() {
  const { t } = useTranslation();
  return (
    <div className="flex items-baseline gap-2 border-b border-border px-1 pb-1 text-[11px] text-subtle">
      <span className="w-44 shrink-0">{t('search.miniColTime')}</span>
      <span className="w-12 shrink-0">{t('search.miniColSeverity')}</span>
      <span className="w-28 shrink-0">{t('search.miniColService')}</span>
      <span className="min-w-0 flex-1">{t('search.miniColBody')}</span>
    </div>
  );
}

/** 紧凑日志列表（链路日志/上下文 tab；点击切换抽屉目标，当前记录高亮）. */
export function MiniLogList({ rows, currentTs, onSelect, showHeader = false }: { rows: DetailRow[]; currentTs: string; onSelect: (row: DetailRow) => void; showHeader?: boolean }) {
  return (
    <div className="flex flex-col">
      {showHeader && <MiniLogHeader />}
      {rows.map((row, index) => {
        const ts = typeof row.ts !== 'undefined' && row.ts !== null ? String(row.ts) : '';
        const isCurrent = ts === currentTs;
        const severity = String(row.severity ?? '');
        return (
          <button
            key={index}
            type="button"
            className={cn(
              'flex items-baseline gap-2 border-b border-border/40 px-1 py-1.5 text-left text-xs transition-colors hover:bg-surface-2/60',
              isCurrent && 'bg-primary-soft/40',
            )}
            onClick={() => onSelect(row)}
          >
            <span className="w-44 shrink-0 whitespace-nowrap font-mono tabular-nums text-subtle">{ts.length > 0 ? formatDateTimeMs(ts) : '-'}</span>
            <span className={cn('w-12 shrink-0 truncate font-mono', SEVERITY_TEXT_CLS[severity] ?? 'text-fg')}>{severity.length > 0 ? severity : '-'}</span>
            <span className="w-28 shrink-0 truncate font-mono text-subtle">{String(row.service ?? '-')}</span>
            <span className="min-w-0 flex-1 truncate font-mono text-fg">{String(row.body ?? '-')}</span>
          </button>
        );
      })}
    </div>
  );
}
