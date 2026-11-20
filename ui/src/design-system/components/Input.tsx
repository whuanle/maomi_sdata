import { forwardRef, useId, useState, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from 'react';
import { Eye, EyeOff } from 'lucide-react';
import { cn } from '@/utils/cn';

type InputSize = 'sm' | 'md';

const controlSize: Record<InputSize, string> = {
  sm: 'h-7 text-xs px-2',
  md: 'h-8 text-sm px-2.5',
};

const controlBase =
  'sdata-control rounded-md border bg-surface text-fg outline-none transition-colors placeholder:text-faint ' +
  'focus:border-primary focus:ring-2 focus:ring-primary/20 disabled:cursor-not-allowed disabled:opacity-50 ' +
  'invalid:border-error';

export interface InputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'size' | 'prefix'> {
  size?: InputSize;
  /** 校验失败态（红边，配合 Field 的 error 文案） */
  invalid?: boolean;
  prefix?: ReactNode;
  suffix?: ReactNode;
  /** 测试定位锚点（data-testid） */
  testId?: string;
}

/** 文本输入（v2 替代 antd Input；前后缀插槽用于图标/单位）. */
export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { size = 'md', invalid, prefix, suffix, testId, className, ...rest },
  ref,
) {
  if (!prefix && !suffix) {
    return (
      <input
        ref={ref}
        data-testid={testId}
        aria-invalid={invalid || undefined}
        className={cn(controlBase, controlSize[size], className)}
        {...rest}
      />
    );
  }
  return (
    <div
      className={cn(
        'flex items-center gap-1.5 rounded-md border bg-surface transition-colors',
        'focus-within:border-primary focus-within:ring-2 focus-within:ring-primary/20',
        invalid && 'border-error',
        controlSize[size],
        className,
      )}
      data-testid={testId}
    >
      {prefix && <span className="flex shrink-0 items-center text-subtle">{prefix}</span>}
      <input
        ref={ref}
        aria-invalid={invalid || undefined}
        className="h-full w-full min-w-0 bg-transparent font-[inherit] outline-none placeholder:text-faint"
        {...rest}
      />
      {suffix && <span className="flex shrink-0 items-center text-subtle">{suffix}</span>}
    </div>
  );
});

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  /** 校验失败态（红边） */
  invalid?: boolean;
}

/** 多行文本输入（v2 替代 antd Input.TextArea）. */
export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(function Textarea(
  { invalid, className, rows = 4, ...rest },
  ref,
) {
  return (
    <textarea
      ref={ref}
      rows={rows}
      aria-invalid={invalid || undefined}
      className={cn(controlBase, 'px-2.5 py-1.5 text-sm', className)}
      {...rest}
    />
  );
});

/** 密码输入（可见性切换）. */
export const Password = forwardRef<HTMLInputElement, InputProps>(function Password(props, ref) {
  const [visible, setVisible] = useState(false);
  const toggleId = useId();
  return (
    <Input
      ref={ref}
      type={visible ? 'text' : 'password'}
      suffix={
        <button
          type="button"
          aria-label="toggle password visibility"
          aria-controls={toggleId}
          onClick={() => setVisible((v) => !v)}
          className="cursor-pointer p-0.5 text-subtle hover:text-fg"
        >
          {visible ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
        </button>
      }
      {...props}
    />
  );
});

export interface InputNumberProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'size' | 'prefix' | 'type'> {
  size?: InputSize;
  /** 校验失败态（红边） */
  invalid?: boolean;
  /** 测试定位锚点（data-testid） */
  testId?: string;
  min?: number;
  max?: number;
  /** 步长（默认 1） */
  step?: number;
}

/** 数字输入（v2 替代 antd InputNumber；原生 number 语义 + 等宽字体）. */
export const InputNumber = forwardRef<HTMLInputElement, InputNumberProps>(function InputNumber(
  { size = 'md', invalid, testId, className, min, max, step, ...rest },
  ref,
) {
  return (
    <input
      ref={ref}
      type="number"
      min={min}
      max={max}
      step={step}
      data-testid={testId}
      aria-invalid={invalid || undefined}
      className={cn(controlBase, controlSize[size], 'font-mono', className)}
      {...rest}
    />
  );
});
