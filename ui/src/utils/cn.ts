/**
 * className 组合工具：过滤空值后拼接（组件接受 className 追加的统一入口）.
 * 刻意不引入 clsx/tailwind-merge：项目组件粒度固定，简单拼接足够，减少依赖.
 */
export function cn(...classes: Array<string | false | null | undefined>): string {
  return classes.filter(Boolean).join(' ');
}
