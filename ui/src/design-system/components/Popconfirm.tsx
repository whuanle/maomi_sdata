import { useId, useState, type ReactNode } from 'react';
import * as RPopover from '@radix-ui/react-popover';
import { cn } from '@/utils/cn';
import { Button } from './Button';

export interface PopconfirmProps {
  /** 待确认的触发元素（通常是删除按钮，经 asChild 包裹） */
  children: ReactNode;
  title?: ReactNode;
  /** 附加说明（可选，弱化文字） */
  description?: ReactNode;
  okText?: string;
  cancelText?: string;
  /** 危险操作（确认按钮红色，默认 true 以贴合删除场景） */
  danger?: boolean;
  disabled?: boolean;
  side?: 'top' | 'right' | 'bottom' | 'left';
  align?: 'start' | 'center' | 'end';
  onConfirm: () => void | Promise<void>;
  onCancel?: () => void;
}

/**
 * 气泡确认（Radix Popover 封装，v2 替代 antd Popconfirm）。
 * 项目硬规则：删除/批量操作必须经本组件二次确认。
 */
export function Popconfirm({
  children,
  title,
  description,
  okText,
  cancelText,
  danger = true,
  disabled,
  side = 'top',
  align = 'center',
  onConfirm,
  onCancel,
}: PopconfirmProps) {
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const titleId = useId();

  const close = (confirmed: boolean) => {
    setOpen(false);
    if (confirmed) void onConfirm();
    else onCancel?.();
  };

  /** 确认：onConfirm 可为 async，失败（reject）时保持弹层打开，错误提示交给调用方 toast */
  const handleConfirm = async () => {
    setPending(true);
    try {
      await onConfirm();
      setOpen(false);
    } finally {
      setPending(false);
    }
  };
  return (
    <RPopover.Root open={open} onOpenChange={setOpen}>
      <RPopover.Trigger asChild disabled={disabled}>
        {children}
      </RPopover.Trigger>
      <RPopover.Portal>
        <RPopover.Content
          side={side}
          align={align}
          sideOffset={6}
          aria-labelledby={titleId}
          className={cn(
            'sdata-pop z-[1200] w-60 rounded-lg border border-border bg-surface p-3 shadow-[var(--c-shadow)]',
          )}
        >
          <div id={titleId} className="text-sm font-medium text-fg">
            {title}
          </div>
          {description && <div className="mt-1 text-xs leading-relaxed text-muted">{description}</div>}
          <div className="mt-3 flex justify-end gap-2">
            <Button size="sm" onClick={() => close(false)}>
              {cancelText}
            </Button>
            <Button size="sm" variant="primary" danger={danger} loading={pending} onClick={() => void handleConfirm()}>
              {okText}
            </Button>
          </div>
        </RPopover.Content>
      </RPopover.Portal>
    </RPopover.Root>
  );
}
