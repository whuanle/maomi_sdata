import type { ReactNode } from 'react';
import { cn } from '@/utils/cn';

export interface FieldProps {
  label?: ReactNode;
  /** 必填标记（红色 *，仅视觉；校验由 react-hook-form rules 承担） */
  required?: boolean;
  /** 校验错误文案（react-hook-form errors.xxx?.message 直传） */
  error?: string;
  /** 补充说明（错误文案存在时让位于错误） */
  help?: ReactNode;
  htmlFor?: string;
  children: ReactNode;
  className?: string;
}

/** 表单字段容器：label + 控件 + 错误/说明（配合 react-hook-form 的 register 模式使用）. */
export function Field({ label, required, error, help, htmlFor, children, className }: FieldProps) {
  return (
    <div className={cn('flex flex-col gap-1', className)}>
      {label && (
        <label htmlFor={htmlFor} className="text-xs font-medium text-muted">
          {required && <span className="mr-0.5 text-error">*</span>}
          {label}
        </label>
      )}
      {children}
      {error ? (
        <div className="text-xs text-error" role="alert">
          {error}
        </div>
      ) : (
        help && <div className="text-xs text-subtle">{help}</div>
      )}
    </div>
  );
}
