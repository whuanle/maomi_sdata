import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { RotateCw } from 'lucide-react';
import { cn } from '@/utils/cn';
import { Button } from './Button';
import { formatDateTime } from '@/utils/format';

/** 页面容器：可选工具条（右对齐）+ 内容（不渲染页面标题文案，页面名由侧边导航承载）. */
export function Page({ extra, children, className }: { extra?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <div className={cn('flex h-full flex-col gap-3', className)}>
      {extra && (
        <div className="flex flex-wrap items-center justify-end gap-2">
          {extra}
        </div>
      )}
      <div className="min-h-0 flex-1">{children}</div>
    </div>
  );
}

/** 查询工具条：左对齐控件流 + 可选刷新按钮（列表页统一入口）. */
export function QueryBar({ children, onRefresh, className }: { children: ReactNode; onRefresh?: () => void; className?: string }) {
  const { t } = useTranslation();
  return (
    <div className={cn('flex flex-wrap items-center gap-2', className)}>
      {children}
      {onRefresh && (
        <Button variant="secondary" icon={<RotateCw className="size-3.5" aria-hidden />} onClick={onRefresh}>
          {t('common.refresh')}
        </Button>
      )}
    </div>
  );
}

export interface StatCardProps {
  title: ReactNode;
  value: ReactNode;
  hint?: ReactNode;
  /** 数值行追加 class（语义着色：成功绿/错误红等） */
  valueClassName?: string;
  className?: string;
}

/** 统计卡片（概览/大屏通用；数值加等宽字体提升数字可读性）. */
export function StatCard({ title, value, hint, valueClassName, className }: StatCardProps) {
  return (
    <div className={cn('rounded-lg border border-border bg-surface px-4 py-3', className)}>
      <div className="text-xs text-subtle">{title}</div>
      <div className={cn('mt-1 truncate font-mono text-2xl leading-8 font-semibold text-fg', valueClassName)}>{value}</div>
      {hint && <div className="mt-0.5 truncate text-xs text-subtle">{hint}</div>}
    </div>
  );
}

export interface AuditCellProps {
  time?: string;
  user?: string;
}

/** 审计单元格：创建/更新时间在上、操作人在下（实体表硬规则）. */
export function AuditCell({ time, user }: AuditCellProps) {
  return (
    <div className="text-xs leading-4">
      <div className="font-mono">{time ? formatDateTime(time) : '-'}</div>
      <div className="text-subtle">{user ?? '-'}</div>
    </div>
  );
}
