import { useState } from 'react';
import { ChevronDown, Copy } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/design-system';
import { cn } from '@/utils/cn';

import type { ChartSource } from './buildChartSql';
import { TIME_SERIES_EXAMPLES } from './timeSeriesSql';

export function TimeSeriesSqlInstructions({ source, onCopy }: { source: ChartSource; onCopy: (sql: string) => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(true);

  return (
    <section className="mt-3 min-w-0 rounded-md border border-border bg-surface-2/50">
      <button type="button" aria-expanded={open} aria-controls="time-series-sql-instructions" onClick={() => setOpen((value) => !value)} className="flex w-full cursor-pointer items-center gap-2 px-3 py-2 text-left text-xs hover:bg-surface-2">
        <ChevronDown aria-hidden className={cn('size-3.5 shrink-0 text-muted transition-transform', !open && '-rotate-90')} />
        {t('charts.sqlInstructions')}
      </button>
      {open && <div id="time-series-sql-instructions" className="space-y-3 px-4 pb-3 text-xs leading-relaxed">
        <div>
          <p className="mb-1 font-semibold">{t('charts.sqlResultColumns')}</p>
          <ul className="list-disc space-y-0.5 pl-5 text-muted">
            <li><code className="text-fg">ts_bucket</code> — {t('charts.sqlTimestampHint')}</li>
            <li><code className="text-fg">value</code> — {t('charts.sqlValueHint')}</li>
            <li><code className="text-fg">series</code> — {t('charts.sqlGroupHint')}</li>
          </ul>
        </div>
        <div>
          <p className="mb-1 font-semibold">{t('charts.sqlParametersTitle')}</p>
          <p className="text-muted">{t('charts.sqlParametersHint')}</p>
          <p className="mt-1 text-muted">{t('charts.sqlSourceHint')}</p>
        </div>
        <div className="overflow-hidden rounded border border-border/60 bg-surface">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border/60 px-3 py-1">
            <span className="text-muted">{t(`charts.sqlExamples.${source}`)}</span>
            <Button variant="ghost" size="sm" icon={<Copy className="size-3.5" />} onClick={() => onCopy(TIME_SERIES_EXAMPLES[source])}>{t('charts.copySqlExample')}</Button>
          </div>
          <pre className="overflow-x-auto px-3 py-2 text-xs leading-relaxed text-fg"><code>{TIME_SERIES_EXAMPLES[source]}</code></pre>
        </div>
      </div>}
    </section>
  );
}
