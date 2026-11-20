import type { ReactNode } from 'react';
import * as RDropdown from '@radix-ui/react-dropdown-menu';
import { cn } from '@/utils/cn';

export interface DropdownMenuItemProps {
  icon?: ReactNode;
  danger?: boolean;
  disabled?: boolean;
  onSelect?: () => void;
  children: ReactNode;
}

/**
 * 下拉菜单（Radix DropdownMenu 封装，v2 替代 antd Dropdown）。
 * 复合用法：DropdownMenu.Root > Trigger > Content > Item...
 */
export const DropdownMenu = {
  Root: RDropdown.Root,
  Trigger: RDropdown.Trigger,
  Group: RDropdown.Group,
  Label: ({ children, className }: { children: ReactNode; className?: string }) => (
    <RDropdown.Label className={cn('px-2 py-1.5 text-xs font-medium text-subtle', className)}>
      {children}
    </RDropdown.Label>
  ),
  Separator: () => <RDropdown.Separator className="my-1 h-px bg-[var(--c-border)]" />,
  Content: ({
    children,
    className,
    align = 'start',
    side = 'bottom',
  }: {
    children: ReactNode;
    className?: string;
    align?: 'start' | 'center' | 'end';
    side?: 'top' | 'right' | 'bottom' | 'left';
  }) => (
    <RDropdown.Portal>
      <RDropdown.Content
        side={side}
        align={align}
        sideOffset={4}
        className={cn(
          'sdata-pop z-[1200] min-w-36 rounded-md border border-border bg-surface p-1 shadow-[var(--c-shadow)]',
          className,
        )}
      >
        {children}
      </RDropdown.Content>
    </RDropdown.Portal>
  ),
  Item: ({ icon, danger, disabled, onSelect, children, className }: DropdownMenuItemProps & { className?: string }) => (
    <RDropdown.Item
      disabled={disabled}
      onSelect={(event) => {
        // Radix 默认聚焦到触发器会吞掉后续焦点，保持默认行为但交还给业务回调
        if (onSelect) {
          event.preventDefault();
          onSelect();
        }
      }}
      className={cn(
        'flex cursor-pointer select-none items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-none',
        'data-[highlighted]:bg-primary-soft data-[disabled]:pointer-events-none data-[disabled]:opacity-50',
        danger ? 'text-error' : 'text-fg',
        className,
      )}
    >
      {icon && <span className="flex size-3.5 items-center justify-center [& svg]:size-3.5">{icon}</span>}
      {children}
    </RDropdown.Item>
  ),
};
