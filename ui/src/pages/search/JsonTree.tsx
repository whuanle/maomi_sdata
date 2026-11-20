import { useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { cn } from '@/utils/cn';

/** 容器/叶子判定. */
function isContainer(value: unknown): value is Record<string, unknown> | unknown[] {
  return value !== null && typeof value === 'object';
}

/** 叶子值语义着色（字符串绿/数字蓝/布尔橙/null 淡）. */
function leafClass(value: unknown): string {
  if (typeof value === 'string') {
    return 'text-success';
  }

  if (typeof value === 'number') {
    return 'text-info';
  }

  if (typeof value === 'boolean') {
    return 'text-warning';
  }

  return 'text-faint';
}

/** 叶子值文本（字符串带引号，HyperDX 式）. */
function formatLeaf(value: unknown): string {
  if (typeof value === 'string') {
    return JSON.stringify(value);
  }

  if (value === null) {
    return 'null';
  }

  return String(value);
}

/** 单节点（对象/数组容器可点击收展；子节点缩进 + 左侧细线）. */
function TreeNode({ name, value, defaultExpanded }: { name: string; value: unknown; defaultExpanded: boolean }) {
  const [expanded, setExpanded] = useState(defaultExpanded);
  const container = isContainer(value);
  const childEntries = container ? Object.entries(value) : [];
  const sizeText = Array.isArray(value) ? `${value.length} items` : `${childEntries.length} keys`;

  return (
    <div>
      <div className="flex items-baseline gap-1.5">
        {container ? (
          <button
            type="button"
            aria-label={`${expanded ? 'collapse' : 'expand'}: ${name}`}
            className="shrink-0 cursor-pointer self-center rounded-sm p-0.5 text-subtle outline-none hover:text-fg focus-visible:ring-2 focus-visible:ring-primary/30"
            onClick={() => setExpanded((prev) => !prev)}
          >
            <ChevronDown className={cn('size-3 transition-transform', !expanded && '-rotate-90')} aria-hidden />
          </button>
        ) : (
          <span className="w-4 shrink-0 self-center" aria-hidden />
        )}
        <span className="font-mono text-xs text-primary">{name}</span>
        {container ? (
          <button
            type="button"
            className="cursor-pointer font-mono text-xs text-subtle hover:text-fg"
            onClick={() => setExpanded((prev) => !prev)}
          >
            {Array.isArray(value) ? `[${sizeText}]` : `{${sizeText}}`}
          </button>
        ) : (
          <span className={cn('font-mono text-xs break-all', leafClass(value))}>{formatLeaf(value)}</span>
        )}
      </div>
      {container && expanded && childEntries.length > 0 && (
        <div className="ml-[7px] flex flex-col border-l border-border/40 pl-3">
          {childEntries.map(([key, child]) => (
            <TreeNode key={key} name={key} value={child} defaultExpanded={defaultExpanded} />
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * 递归可折叠 JSON 树（ClickStack/HyperDX 式：容器节点 `{n} keys` 可点击收展，叶子按类型着色）.
 * rootName 传入时根容器本身也渲染为可收展节点（抽屉列值的 JSON 列，HyperDX `LogAttributes {} 4 keys` 式）；
 * 缺省时根节点的各键平铺为顶层（行内 JSON 预览，整行对象直接铺开）.
 */
export function JsonTree({
  value,
  defaultExpanded = true,
  rootName,
  className,
}: {
  value: unknown;
  defaultExpanded?: boolean;
  rootName?: string;
  className?: string;
}) {
  if (!isContainer(value)) {
    return <pre className={cn('font-mono text-xs break-all whitespace-pre-wrap text-fg', className)}>{formatLeaf(value)}</pre>;
  }

  if (rootName !== undefined) {
    return (
      <div className={cn('flex flex-col', className)}>
        <TreeNode name={rootName} value={value} defaultExpanded={defaultExpanded} />
      </div>
    );
  }

  return (
    <div className={cn('flex flex-col', className)}>
      {Object.entries(value).map(([key, child]) => (
        <TreeNode key={key} name={key} value={child} defaultExpanded={defaultExpanded} />
      ))}
    </div>
  );
}
