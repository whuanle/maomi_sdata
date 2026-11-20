import { create } from 'zustand';
import { CircleCheck, CircleX, Info, TriangleAlert, type LucideIcon } from 'lucide-react';
import { cn } from '@/utils/cn';

type ToastTone = 'success' | 'error' | 'info' | 'warning';

interface ToastItem {
  id: number;
  tone: ToastTone;
  content: string;
  duration: number;
}

interface ToastState {
  items: ToastItem[];
  push: (tone: ToastTone, content: string, duration?: number) => void;
  dismiss: (id: number) => void;
}

let nextToastId = 1;

/** 全局 toast 队列（替代 antd App.useApp() 的 message，页面经 toast.success(...) 直接调用）. */
export const useToastStore = create<ToastState>()((set) => ({
  items: [],
  push: (tone, content, duration) => {
    const id = nextToastId++;
    const ttl = duration ?? (tone === 'error' ? 4000 : 3000);
    set((state) => ({ items: [...state.items, { id, tone, content, duration: ttl }] }));
    window.setTimeout(() => {
      set((state) => ({ items: state.items.filter((item) => item.id !== id) }));
    }, ttl);
  },
  dismiss: (id) => set((state) => ({ items: state.items.filter((item) => item.id !== id) })),
}));

/** 命令式入口：`toast.success('已保存')`（渲染由 <Toaster/> 承担，挂在 AppProviders）. */
export const toast = {
  success: (content: string, duration?: number) => useToastStore.getState().push('success', content, duration),
  error: (content: string, duration?: number) => useToastStore.getState().push('error', content, duration),
  info: (content: string, duration?: number) => useToastStore.getState().push('info', content, duration),
  warning: (content: string, duration?: number) => useToastStore.getState().push('warning', content, duration),
};

const toneIcon: Record<ToastTone, { Icon: LucideIcon; className: string }> = {
  success: { Icon: CircleCheck, className: 'text-success' },
  error: { Icon: CircleX, className: 'text-error' },
  info: { Icon: Info, className: 'text-info' },
  warning: { Icon: TriangleAlert, className: 'text-warning' },
};

/** Toast 渲染层：固定右上角，挂一次即可（AppProviders）. */
export function Toaster() {
  const items = useToastStore((state) => state.items);
  const dismiss = useToastStore((state) => state.dismiss);
  if (items.length === 0) return null;
  return (
    <div className="pointer-events-none fixed top-4 right-4 z-[2000] flex w-80 flex-col gap-2">
      {items.map((item) => {
        const { Icon, className } = toneIcon[item.tone];
        return (
          <div
            key={item.id}
            role="status"
            className="sdata-toast pointer-events-auto flex items-start gap-2 rounded-lg border border-border bg-surface px-3 py-2.5 text-sm text-fg shadow-[var(--c-shadow)]"
          >
            <Icon className={cn('mt-0.5 size-4 shrink-0', className)} aria-hidden />
            <span className="min-w-0 flex-1 break-words">{item.content}</span>
            <button
              type="button"
              aria-label="dismiss"
              onClick={() => dismiss(item.id)}
              className="cursor-pointer text-faint transition-colors hover:text-fg"
            >
              ×
            </button>
          </div>
        );
      })}
    </div>
  );
}
