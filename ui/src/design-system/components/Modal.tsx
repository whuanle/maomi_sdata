import { useState, type ReactNode } from 'react';
import * as RDialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { cn } from '@/utils/cn';
import { Button } from './Button';

export interface ModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title?: ReactNode;
  /** 底部操作区（不传则无 footer） */
  footer?: ReactNode;
  /** 内容宽度（px 数字或 CSS 值，默认 520） */
  width?: number | string;
  /** 右上角关闭按钮（默认显示） */
  closable?: boolean;
  children: ReactNode;
  className?: string;
}

/**
 * 模态框（Radix Dialog 封装，v2 替代 antd Modal）。
 * 点击遮罩或按 Esc 均可关闭（Radix 默认行为，勿用 onInteractOutside 拦截）。
 */
export function Modal({
  open,
  onOpenChange,
  title,
  footer,
  width = 520,
  closable = true,
  children,
  className,
}: ModalProps) {
  return (
    <RDialog.Root open={open} onOpenChange={onOpenChange}>
      <RDialog.Portal>
        <RDialog.Overlay className="sdata-overlay fixed inset-0 z-[1100] bg-[var(--c-overlay)]" />
        <RDialog.Content
          style={{ width: typeof width === 'number' ? `${width}px` : width, maxWidth: '92vw' }}
          className={cn(
            'fixed top-1/2 left-1/2 z-[1100] flex max-h-[85vh] -translate-x-1/2 -translate-y-1/2 flex-col',
            'rounded-lg border border-border bg-surface shadow-[var(--c-shadow)]',
            className,
          )}
        >
          {title !== undefined && (
            <div className="flex shrink-0 items-center justify-between border-b border-border px-4 py-3">
              <RDialog.Title className="text-[15px] font-semibold text-fg">{title}</RDialog.Title>
              {closable && (
                <RDialog.Close asChild>
                  <button
                    type="button"
                    aria-label="close"
                    className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
                  >
                    <X className="size-4" aria-hidden />
                  </button>
                </RDialog.Close>
              )}
            </div>
          )}
          <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3">{children}</div>
          {footer && <div className="flex shrink-0 justify-end gap-2 border-t border-border px-4 py-3">{footer}</div>}
        </RDialog.Content>
      </RDialog.Portal>
    </RDialog.Root>
  );
}

export interface ConfirmModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  /** 正文说明（可选） */
  content?: ReactNode;
  okText?: string;
  cancelText?: string;
  /** 危险操作（确认按钮为红色） */
  danger?: boolean;
  onOk: () => void | Promise<void>;
}

/** 确认弹窗（原 antd Modal.confirm 用法的组件化替代，onOk 支持 async，内部管理 pending）. */
export function ConfirmModal({
  open,
  onOpenChange,
  title,
  content,
  okText,
  cancelText,
  danger,
  onOk,
}: ConfirmModalProps) {
  const [pending, setPending] = useState(false);
  const handleOk = async () => {
    setPending(true);
    try {
      await onOk();
      onOpenChange(false);
    } finally {
      setPending(false);
    }
  };
  return (
    <Modal
      open={open}
      onOpenChange={(next) => {
        if (!pending) onOpenChange(next);
      }}
      title={title}
      width={420}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{cancelText}</Button>
          <Button variant="primary" danger={danger} loading={pending} onClick={() => void handleOk()}>
            {okText}
          </Button>
        </>
      }
    >
      {content && <div className="text-sm text-muted">{content}</div>}
    </Modal>
  );
}
