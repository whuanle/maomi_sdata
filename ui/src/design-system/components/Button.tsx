import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import { LoaderCircle } from 'lucide-react';
import { cn } from '@/utils/cn';

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'link' | 'accent' | 'accentOutline' | 'accentSoft';
type ButtonSize = 'sm' | 'md';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** 破坏性操作（删除等）：主色换语义红 */
  danger?: boolean;
  icon?: ReactNode;
  loading?: boolean;
  block?: boolean;
  /** 测试定位锚点（data-testid） */
  testId?: string;
}

const sizeClasses: Record<ButtonSize, string> = {
  sm: 'h-7 px-2.5 text-xs gap-1 rounded-sm',
  md: 'h-8 px-3 text-sm gap-1.5 rounded-md',
};

/** 基础按钮：variant × danger × loading 组合（v2 唯一按钮入口，替代 antd Button）. */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', danger, icon, loading, block, className, children, disabled, testId, ...rest },
  ref,
) {
  return (
    <button
      ref={ref}
      type={rest.type ?? 'button'}
      disabled={disabled || loading}
      data-testid={testId}
      className={cn(
        'inline-flex select-none items-center justify-center whitespace-nowrap border font-medium transition-colors',
        'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-primary',
        'disabled:pointer-events-none disabled:opacity-50',
        sizeClasses[size],
        variant === 'primary' &&
          (danger
            ? 'border-transparent bg-error text-white hover:bg-error/85 active:bg-error/75'
            : 'border-transparent bg-primary text-white hover:bg-primary-hover active:bg-primary-active'),
        variant === 'secondary' &&
          (danger
            ? 'border-border bg-surface text-error hover:border-error/50 hover:bg-error-soft'
            : 'border-border bg-surface text-fg hover:border-border-strong hover:bg-surface-2'),
        variant === 'ghost' &&
          (danger
            ? 'border-transparent bg-transparent text-error hover:bg-error-soft'
            : 'border-transparent bg-transparent text-muted hover:bg-surface-2 hover:text-fg'),
        variant === 'link' &&
          (danger
            ? 'border-transparent bg-transparent px-0.5 text-error hover:underline'
            : 'border-transparent bg-transparent px-0.5 text-primary hover:text-primary-hover hover:underline'),
        variant === 'accent' &&
          'border-accent bg-accent text-bg hover:border-accent/85 hover:bg-accent/85 active:bg-accent/75',
        // HyperDX 式描边强调：普通灰框白底 + accent 图标文字（如 Live Tail）
        variant === 'accentOutline' &&
          'border-border bg-surface text-accent hover:border-accent/50 hover:bg-accent-soft active:bg-accent-soft/70',
        // 描边强调的激活态：accent 软底 + accent 描边
        variant === 'accentSoft' &&
          'border-accent/60 bg-accent-soft text-accent hover:border-accent hover:bg-accent-soft/80 active:bg-accent-soft/60',
        block && 'w-full',
        className,
      )}
      {...rest}
    >
      {loading ? <LoaderCircle className="size-3.5 animate-spin" aria-hidden /> : icon}
      {children}
    </button>
  );
});
