import type { ReactNode } from 'react';
import * as RTabs from '@radix-ui/react-tabs';
import { cn } from '@/utils/cn';

export interface TabsProps {
  value?: string;
  defaultValue?: string;
  onValueChange?: (value: string) => void;
  children: ReactNode;
  className?: string;
}

/** 页签根（受控/非受控，v2 替代 antd Tabs）. */
export function Tabs({ value, defaultValue, onValueChange, children, className }: TabsProps) {
  return (
    <RTabs.Root value={value} defaultValue={defaultValue} onValueChange={onValueChange} className={className}>
      {children}
    </RTabs.Root>
  );
}

/** 页签条（胶囊分段式：激活项浮起为 surface 底）. */
export function TabsList({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <RTabs.List
      className={cn('inline-flex max-w-full items-center gap-0.5 overflow-x-auto rounded-md bg-surface-2 p-0.5', className)}
    >
      {children}
    </RTabs.List>
  );
}

export function TabsTrigger({ value, children, className }: { value: string; children: ReactNode; className?: string }) {
  return (
    <RTabs.Trigger
      value={value}
      className={cn(
        'shrink-0 whitespace-nowrap rounded-sm px-3 py-1 text-sm text-muted transition-colors',
        'outline-none hover:text-fg focus-visible:ring-2 focus-visible:ring-primary/30',
        'data-[state=active]:bg-surface data-[state=active]:font-medium data-[state=active]:text-fg',
        'data-[state=active]:shadow-[var(--c-shadow-sm)]',
        className,
      )}
    >
      {children}
    </RTabs.Trigger>
  );
}

export function TabsContent({
  value,
  children,
  className,
}: {
  value: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <RTabs.Content value={value} className={cn('outline-none', className)}>
      {children}
    </RTabs.Content>
  );
}
