import { useId, useState } from 'react';
import { ChevronDown, CircleHelp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/design-system';
import type { ChartSource as SearchDomain } from '@/api/chartTypes';
import { cn } from '@/utils/cn';

export const LUCENE_HELP_EXAMPLES = [
  { key: 'text', value: 'timeout' },
  { key: 'phrase', value: '"connection timeout"' },
  { key: 'exact', value: 'service:"api gateway"' },
  { key: 'prefix', value: 'service:api*' },
  { key: 'exclude', value: '-service:internal' },
  { key: 'and', value: 'service:api timeout' },
  { key: 'or', value: 'service:api OR service:web' },
] as const;

export function luceneHelpExamples(source: SearchDomain) {
  if (source === 'iot') return [
    { key: 'text', value: 'temperature' },
    { key: 'exact', value: 'device_key:sensor-01' },
    { key: 'prefix', value: 'signal:temperature*' },
    { key: 'numeric', value: 'num_value:>20' },
  ];
  if (source === 'metrics') return [
    { key: 'text', value: 'cpu' },
    { key: 'phrase', value: '"system cpu"' },
    { key: 'exact', value: 'name:"system cpu"' },
    { key: 'prefix', value: 'name:cpu*' },
    { key: 'exclude', value: '-name:internal' },
    { key: 'and', value: 'name:cpu system' },
    { key: 'or', value: 'name:cpu OR name:memory' },
    { key: 'numeric', value: 'value:>80' },
  ];
  if (source === 'sessions') return [
    { key: 'text', value: 'Chrome' },
    { key: 'phrase', value: '"Mozilla/5.0"' },
    { key: 'exact', value: 'user_agent:"Mozilla/5.0"' },
    { key: 'prefix', value: 'domain:api*' },
    { key: 'exclude', value: '-domain:internal' },
    { key: 'and', value: 'domain:example.org Chrome' },
    { key: 'or', value: 'domain:example.org OR domain:example.net' },
  ];
  return LUCENE_HELP_EXAMPLES;
}

export function LuceneSearchHelp({ source, value, context = 'search' }: { source: SearchDomain; value: string; context?: 'search' | 'chart' }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(true);
  const contentId = useId();
  const contextKey = context === 'chart' ? 'search.luceneHelp.chart' : 'search.luceneHelp';

  return (
    <section aria-label={t('search.luceneHelp.title')} className="mb-2 shrink-0 rounded-md border border-border bg-surface text-xs" data-testid={context === 'chart' ? 'chart-lucene-help' : 'search-lucene-help'}>
      <div className="flex items-start justify-between gap-2 px-3 py-1">
        <p className="min-w-0 self-center break-words leading-relaxed">
          <span className="font-semibold">{t('search.luceneHelp.current')} </span>
          {value.trim()
            ? <><code className="whitespace-pre-wrap break-all font-mono">{value}</code><span className="ml-2 text-muted">{t(`${contextKey}.draft`)}</span></>
            : <span className="text-muted">{t(`${contextKey}.empty`)}</span>}
        </p>
        <Button variant="link" size="sm" icon={<CircleHelp className="size-3.5" aria-hidden />} aria-expanded={open} aria-controls={contentId} onClick={() => setOpen((previous) => !previous)}>
          {t('search.luceneHelp.title')}
          <ChevronDown aria-hidden className={cn('size-3 shrink-0 transition-transform', !open && '-rotate-90')} />
        </Button>
      </div>
      {open && <div id={contentId} className="space-y-2 border-t border-border px-3 py-2">
        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-2">
          <span className="text-muted">{t('search.luceneHelp.examples')}</span>
          {luceneHelpExamples(source).map(({ key, value: example }) => (
            <span key={key} className="inline-flex max-w-full flex-wrap items-baseline gap-1">
              <span className="text-muted">{t(`search.luceneHelp.${key}`)}</span>
              <code className="max-w-full break-all rounded-sm bg-surface-2 px-1 py-0.5 font-mono text-fg">{example}</code>
            </span>
          ))}
        </div>
        <p className="text-muted">{t(`search.luceneHelp.scope.${source}`)} {t(`${contextKey}.existence`)}</p>
      </div>}
    </section>
  );
}
