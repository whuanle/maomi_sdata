import type { ReactNode } from 'react';
import * as RSelect from '@radix-ui/react-select';
import { Check, ChevronDown } from 'lucide-react';
import { cn } from '@/utils/cn';

export interface SelectOption {
  label: ReactNode;
  value: string;
  disabled?: boolean;
}

export interface SelectGroup {
  label: ReactNode;
  options: SelectOption[];
}

export interface SelectProps {
  options?: SelectOption[];
  /** 分组选项（与 options 二选一或混用，分组渲染在前） */
  groups?: SelectGroup[];
  value?: string;
  defaultValue?: string;
  onValueChange?: (value: string) => void;
  placeholder?: ReactNode;
  disabled?: boolean;
  /** 下拉展开方向与对齐（默认向下、与触发器同宽起点对齐） */
  side?: 'bottom' | 'top';
  align?: 'start' | 'center' | 'end';
  /** 触发器追加 class（控制宽度等） */
  className?: string;
  contentClassName?: string;
  /** 测试定位锚点（透传到触发器 data-testid） */
  testId?: string;
  'aria-label'?: string;
}

const triggerBase =
  'sdata-control flex h-8 items-center justify-between gap-1 rounded-md border border-border bg-surface px-2.5 ' +
  'text-sm text-fg outline-none transition-colors data-[placeholder]:text-faint ' +
  'hover:border-border-strong focus:border-primary focus:ring-2 focus:ring-primary/20 ' +
  'disabled:cursor-not-allowed disabled:opacity-50 aria-expanded:border-primary';

/**
 * 下拉选择（Radix Select 封装，v2 替代 antd Select）。
 * 选项集合由 options/groups 提供；浮层与触发器同宽，超出可滚动。
 */
export function Select({
  options,
  groups,
  value,
  defaultValue,
  onValueChange,
  placeholder,
  disabled,
  side = 'bottom',
  align = 'start',
  className,
  contentClassName,
  testId,
  'aria-label': ariaLabel,
}: SelectProps) {
  const allItems = [...(groups ?? []).flatMap((g) => g.options), ...(options ?? [])];
  const selected = allItems.find((item) => item.value === value);
  return (
    <RSelect.Root value={value} defaultValue={defaultValue} onValueChange={onValueChange} disabled={disabled}>
      <RSelect.Trigger data-testid={testId} className={cn(triggerBase, className)} aria-label={ariaLabel}>
        <span className="min-w-0 truncate">
          {selected ? selected.label : <RSelect.Value placeholder={placeholder} />}
        </span>
        <RSelect.Icon>
          <ChevronDown className="size-3.5 shrink-0 text-subtle" aria-hidden />
        </RSelect.Icon>
      </RSelect.Trigger>
      <RSelect.Portal>
        <RSelect.Content
          position="popper"
          side={side}
          align={align}
          sideOffset={4}
          className={cn(
            'sdata-pop z-[1200] max-h-72 min-w-[var(--radix-select-trigger-width)] overflow-auto rounded-md border',
            'border-border bg-surface py-1 shadow-[var(--c-shadow)]',
            contentClassName,
          )}
        >
          <RSelect.Viewport>
            {groups?.map((group) => (
              <RSelect.Group key={String(group.label)}>
                <RSelect.Label className="px-2 py-1.5 text-xs font-medium text-subtle">{group.label}</RSelect.Label>
                {group.options.map((item) => (
                  <SelectItem key={item.value} item={item} />
                ))}
              </RSelect.Group>
            ))}
            {options?.map((item) => (
              <SelectItem key={item.value} item={item} />
            ))}
          </RSelect.Viewport>
        </RSelect.Content>
      </RSelect.Portal>
    </RSelect.Root>
  );
}

function SelectItem({ item }: { item: SelectOption }) {
  return (
    <RSelect.Item
      value={item.value}
      disabled={item.disabled}
      className={cn(
        'mx-1 flex cursor-pointer select-none items-center justify-between gap-2 rounded-sm px-2 py-1.5 text-sm',
        'text-fg outline-none data-[highlighted]:bg-primary-soft data-[disabled]:pointer-events-none data-[disabled]:opacity-50',
      )}
    >
      <RSelect.ItemText>{item.label}</RSelect.ItemText>
      <RSelect.ItemIndicator>
        <Check className="size-3.5 text-primary" aria-hidden />
      </RSelect.ItemIndicator>
    </RSelect.Item>
  );
}
