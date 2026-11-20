import { useTranslation } from 'react-i18next';
import type { AnalyticsQueryResponse } from '@/api/types';
import type { ChartSource } from '@/api/chartTypes';
import { formatDateTime } from '@/utils/format';

export function ExplorerResultStatus({ source, range, result, changed }: {
  source: ChartSource;
  range: { from: string; to: string };
  result: AnalyticsQueryResponse | null;
  changed: boolean;
}) {
  const { t } = useTranslation();
  return <div className="space-y-1 border-y border-border/60 py-2 text-xs text-muted">
    <p>{t('charts.executedContext', { source: t(source === 'logs' ? 'search.sourceLogs' : source === 'traces' ? 'search.sourceTraces' : source === 'iot' ? 'charts.sourceMqtt' : source === 'metrics' ? 'charts.sourceMetrics' : 'charts.sourceSessions'), from: formatDateTime(range.from), to: formatDateTime(range.to) })}</p>
    {result && <p className="font-mono">{t('charts.resultSummary', { count: result.rows.length, elapsed: result.elapsedMs })}</p>}
    {changed && <p role="status" className="text-warning">{t('charts.draftNotRun')}</p>}
  </div>;
}
