import type { ReactNode } from 'react';
import * as RTooltip from '@radix-ui/react-tooltip';
import { cn } from '@/utils/cn';

export interface TooltipProps {
  content: ReactNode;
  children: ReactNode;
  side?: 'top' | 'right' | 'bottom' | 'left';
  /** 触发延迟 ms（默认 300，接近 antd 手感） */
  delay?: number;
  /** 以卡片文字说明为主的提示可用 multiline 放宽宽度 */
  multiline?: boolean;
}

/** 文字提示（Radix Tooltip 封装，v2 替代 antd Tooltip；content 为空时不渲染）. */
export function Tooltip({ content, children, side = 'top', delay = 300, multiline }: TooltipProps) {
  if (content === null || content === undefined || content === '') return <>{children}</>;
  return (
    <RTooltip.Provider delayDuration={delay}>
      <RTooltip.Root>
        <RTooltip.Trigger asChild>{children}</RTooltip.Trigger>
        <RTooltip.Portal>
          <RTooltip.Content
            side={side}
            sideOffset={6}
            className={cn(
              'sdata-pop z-[1500] rounded-md border border-border bg-surface-3 px-2 py-1.5 text-xs text-fg',
              'shadow-[var(--c-shadow)]',
              multiline ? 'max-w-72 leading-relaxed' : 'max-w-60',
            )}
          >
            {content}
            <RTooltip.Arrow className="fill-[var(--c-surface-3)]" aria-hidden />
          </RTooltip.Content>
        </RTooltip.Portal>
      </RTooltip.Root>
    </RTooltip.Provider>
  );
}
