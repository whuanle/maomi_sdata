import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp, Copy } from 'lucide-react';
import { Button, Tooltip } from '@/design-system';
import { cn } from '@/utils/cn';

/** 消息体默认限高；超出时可展开为内部滚动区域，避免挤走详情页签. */
export function LogBodyPreview({ text, onCopy }: { text: string; onCopy: (text: string) => void }) {
  const { t } = useTranslation();
  const contentId = useId();
  const contentRef = useRef<HTMLPreElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [overflowing, setOverflowing] = useState(false);

  useEffect(() => {
    const content = contentRef.current;
    if (!content || expanded) return;
    const measure = () => setOverflowing(content.scrollHeight > content.clientHeight + 1);
    measure();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(content);
    window.addEventListener('resize', measure);
    return () => {
      observer?.disconnect();
      window.removeEventListener('resize', measure);
    };
  }, [text, expanded]);

  return (
    <div className="flex min-w-0 max-w-full flex-col gap-1.5">
      <div className="flex items-center justify-between">
        <h4 className="text-xs font-medium text-subtle">{t('search.sectionBody')}</h4>
        <Tooltip content={t('search.copySection')}>
          <button
            type="button"
            aria-label={t('search.copySection')}
            className="cursor-pointer p-0.5 text-faint hover:text-fg"
            onClick={() => onCopy(text)}
          >
            <Copy className="size-3.5" aria-hidden />
          </button>
        </Tooltip>
      </div>
      <pre
        ref={contentRef}
        id={contentId}
        data-testid="log-body-preview"
        className={cn(
          'min-w-0 max-w-full rounded-md border border-border bg-bg px-2.5 py-2 font-mono text-xs break-all whitespace-pre-wrap text-fg',
          expanded ? 'max-h-[50vh] overflow-y-auto' : 'max-h-[min(8rem,20vh)] overflow-hidden',
        )}
      >{text}</pre>
      {overflowing && (
        <Button
          variant="link"
          size="sm"
          className="self-start"
          aria-expanded={expanded}
          aria-controls={contentId}
          icon={expanded ? <ChevronUp className="size-3.5" aria-hidden /> : <ChevronDown className="size-3.5" aria-hidden />}
          onClick={() => setExpanded((value) => !value)}
        >
          {t(expanded ? 'search.collapseBody' : 'search.expandBody')}
        </Button>
      )}
    </div>
  );
}
