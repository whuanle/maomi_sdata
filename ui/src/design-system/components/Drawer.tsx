import type { ReactNode, Ref } from 'react';
import * as RDialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { cn } from '@/utils/cn';

export interface DrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title?: ReactNode;
  /** 底部操作区（不传则无 footer） */
  footer?: ReactNode;
  /** 面板宽度（px 数字或 CSS 值，默认 560） */
  width?: number | string;
  children: ReactNode;
  className?: string;
  /** 内容滚动容器，供触底加载监听使用 */
  bodyRef?: Ref<HTMLDivElement>;
}

/** 侧滑抽屉（Radix Dialog 侧挂变体，v2 替代 antd Drawer；默认右侧滑入）. */
export function Drawer({ open, onOpenChange, title, footer, width = 560, children, className, bodyRef }: DrawerProps) {
  return (
    <RDialog.Root open={open} onOpenChange={onOpenChange}>
      <RDialog.Portal>
        <RDialog.Overlay className="sdata-overlay fixed inset-0 z-[1100] bg-[var(--c-overlay)]" />
        <RDialog.Content
          style={{ width: typeof width === 'number' ? `${width}px` : width, maxWidth: '94vw' }}
          className={cn(
            'sdata-drawer-right fixed top-0 right-0 z-[1100] flex h-full flex-col',
            'border-l border-border bg-surface shadow-[var(--c-shadow)]',
            className,
          )}
        >
          {title !== undefined && (
            <div className="flex shrink-0 items-center justify-between border-b border-border px-4 py-3">
              <RDialog.Title className="text-[15px] font-semibold text-fg">{title}</RDialog.Title>
              <RDialog.Close asChild>
                <button
                  type="button"
                  aria-label="close"
                  className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
                >
                  <X className="size-4" aria-hidden />
                </button>
              </RDialog.Close>
            </div>
          )}
          <div ref={bodyRef} className="min-h-0 flex-1 overflow-y-auto p-4">{children}</div>
          {footer && <div className="flex shrink-0 justify-end gap-2 border-t border-border px-4 py-3">{footer}</div>}
        </RDialog.Content>
      </RDialog.Portal>
    </RDialog.Root>
  );
}
