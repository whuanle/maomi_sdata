import type { ReactNode } from 'react';
import * as RPopover from '@radix-ui/react-popover';
import { cn } from '@/utils/cn';

/**
 * 通用气泡卡（Radix Popover 封装）：点击弹出的非确认型浮层（表结构面板、字段选择器等）。
 * 复合用法：Popover.Root > Popover.Trigger(asChild) > Popover.Content。
 * 需要二次确认语义的场景用 Popconfirm。
 */
export const Popover = {
  Root: RPopover.Root,
  Trigger: RPopover.Trigger,
  Close: RPopover.Close,
  /** 锚定元素（不作为开合触发器）：combobox 式输入框建议弹层用 Anchor 包住 Input */
  Anchor: RPopover.Anchor,
  Content: ({
    children,
    className,
    align = 'start',
    side = 'bottom',
    onOpenAutoFocus,
  }: {
    children: ReactNode;
    className?: string;
    align?: 'start' | 'center' | 'end';
    side?: 'top' | 'right' | 'bottom' | 'left';
    /** combobox 式弹层传 (e) => e.preventDefault() 把焦点留在锚点输入框 */
    onOpenAutoFocus?: (event: Event) => void;
  }) => (
    <RPopover.Portal>
      <RPopover.Content
        align={align}
        side={side}
        sideOffset={6}
        onOpenAutoFocus={onOpenAutoFocus}
        className={cn(
          'sdata-pop z-[1200] rounded-lg border border-border bg-surface p-3 shadow-[var(--c-shadow)]',
          className,
        )}
      >
        {children}
      </RPopover.Content>
    </RPopover.Portal>
  ),
};
