import { forwardRef, useCallback, type InputHTMLAttributes, type ReactNode } from 'react';
import { cn } from '@/utils/cn';

export interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  /** 右侧文字标签 */
  label?: ReactNode;
  /** 半选态（过滤器树排除语义；与 checked=false 组合呈现横线） */
  indeterminate?: boolean;
}

/** 复选框（原生 input + accent-color，v2 替代 antd Checkbox）. */
export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox(
  { label: explicitLabel, children, className, indeterminate, ...rest },
  forwardedRef,
) {
  // 合并 indeterminate 同步与外部 ref（indeterminate 只能经 DOM 属性设置）
  const setControl = useCallback(
    (node: HTMLInputElement | null) => {
      if (node) {
        node.indeterminate = indeterminate ?? false;
      }

      if (typeof forwardedRef === 'function') {
        forwardedRef(node);
      } else if (forwardedRef) {
        forwardedRef.current = node;
      }
    },
    [indeterminate, forwardedRef],
  );

  const label = explicitLabel ?? children;
  const control = (
    <input
      ref={setControl}
      type="checkbox"
      className={cn('size-3.5 shrink-0 cursor-pointer accent-primary disabled:cursor-not-allowed', className)}
      {...rest}
    />
  );
  if (!label) return control;
  return (
    <label className="inline-flex cursor-pointer select-none items-center gap-1.5 text-sm text-fg">
      {control}
      <span>{label}</span>
    </label>
  );
});
