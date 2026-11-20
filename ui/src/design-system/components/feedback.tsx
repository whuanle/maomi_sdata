import type { ReactNode } from 'react';
import { Inbox, LoaderCircle } from 'lucide-react';
import { cn } from '@/utils/cn';

/** 加载指示（v2 替代 antd Spin）. */
export function Spin({ className }: { className?: string }) {
  return <LoaderCircle className={cn('size-5 animate-spin text-subtle', className)} aria-label="loading" />;
}

/** 内容区居中加载（表格/面板 loading 状态用）. */
export function SpinArea({ tip, className }: { tip?: ReactNode; className?: string }) {
  return (
    <div className={cn('flex h-full min-h-32 flex-col items-center justify-center gap-2', className)}>
      <Spin />
      {tip && <div className="text-xs text-subtle">{tip}</div>}
    </div>
  );
}

export interface EmptyProps {
  /** 说明文案（默认走 common.empty 的语义由调用方传） */
  description?: ReactNode;
  children?: ReactNode;
  className?: string;
}

/** 空态（v2 替代 antd Empty）. */
export function Empty({ description, children, className }: EmptyProps) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-2 py-10 text-subtle', className)}>
      <Inbox className="size-8 text-faint" aria-hidden />
      {description && <div className="text-sm">{description}</div>}
      {children}
    </div>
  );
}
