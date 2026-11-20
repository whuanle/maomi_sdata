import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { cn } from '@/utils/cn';

export const MARKDOWN_MAX_LENGTH = 50_000;

/** 静态内容共用渲染，不执行原始 HTML 或不安全的 URL. */
export function Markdown({ content, className }: { content: string; className?: string }) {
  return <div className={cn(
    'min-w-0 break-words text-sm leading-relaxed text-fg [&>*:first-child]:mt-0 [&>*:last-child]:mb-0 [&_h1]:my-4 [&_h1]:text-2xl [&_h1]:font-semibold [&_h2]:my-3 [&_h2]:text-xl [&_h2]:font-semibold [&_h3]:my-3 [&_h3]:text-lg [&_h3]:font-semibold [&_h4]:my-2 [&_h4]:font-semibold [&_h5]:my-2 [&_h5]:font-semibold [&_h6]:my-2 [&_h6]:font-semibold [&_p]:my-3 [&_ul]:my-3 [&_ul]:list-disc [&_ul]:pl-6 [&_ol]:my-3 [&_ol]:list-decimal [&_ol]:pl-6 [&_li>p]:my-1 [&_blockquote]:my-3 [&_blockquote]:border-l-2 [&_blockquote]:border-border [&_blockquote]:pl-4 [&_blockquote]:text-muted [&_pre]:my-3 [&_pre]:overflow-auto [&_pre]:rounded [&_pre]:bg-surface-2 [&_pre]:p-3 [&_code]:rounded [&_code]:bg-surface-2 [&_code]:px-1 [&_code]:font-mono [&_code]:text-xs [&_pre_code]:p-0 [&_a]:text-primary [&_a]:underline [&_hr]:my-4 [&_hr]:border-border [&_table]:w-full [&_table]:border-collapse [&_th]:border [&_th]:border-border [&_th]:bg-surface-2 [&_th]:p-2 [&_th]:text-left [&_td]:border [&_td]:border-border [&_td]:p-2 [&_img]:max-w-full',
    className,
  )}>
    <ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml components={{
      a: ({ href, children }) => <a href={href} target="_blank" rel="noopener noreferrer">{children}</a>,
      table: ({ children }) => <div className="overflow-auto"><table>{children}</table></div>,
      img: ({ src, alt }) => src ? <img src={src} alt={alt ?? ''} loading="lazy" referrerPolicy="no-referrer" /> : <span>{alt}</span>,
    }}>{content}</ReactMarkdown>
  </div>;
}
