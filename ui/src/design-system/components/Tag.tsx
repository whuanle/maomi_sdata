import type { ReactNode } from 'react';
import { cn } from '@/utils/cn';

type TagTone = 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info';

export interface TagProps {
  tone?: TagTone;
  children: ReactNode;
  className?: string;
}

const toneClasses: Record<TagTone, string> = {
  default: 'border-border bg-surface-2 text-muted',
  primary: 'border-primary/30 bg-primary-soft text-primary',
  success: 'border-success/30 bg-success-soft text-success',
  warning: 'border-warning/30 bg-warning-soft text-warning',
  error: 'border-error/30 bg-error-soft text-error',
  info: 'border-info/30 bg-info-soft text-info',
};

/** 标签（v2 替代 antd Tag：语义色调 soft 底 + 同色文字）. */
export function Tag({ tone = 'default', children, className }: TagProps) {
  return (
    <span
      className={cn(
        'inline-flex select-none items-center gap-1 rounded-sm border px-1.5 py-0.5 text-xs leading-none whitespace-nowrap',
        toneClasses[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}
