import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SpinArea, Tabs, TabsContent, TabsList, TabsTrigger, toast } from '@/design-system';
import { OverviewTab, ValuesTab } from './LogDetailDrawer';
import { ErrorHint, type DetailRow } from './detailShared';
import { loadSpan } from '@/api/traceSearch';

export function SpanPreview({ row, onFilterByValue, onFilterOnlyValue = onFilterByValue, onTraceActivate = () => undefined }: {
  row: DetailRow;
  onFilterByValue: (field: string, value: unknown) => void;
  onFilterOnlyValue?: (field: string, value: unknown) => void;
  onTraceActivate?: (traceId: string) => void;
}) {
  const { t } = useTranslation();
  const [record, setRecord] = useState(row);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [tab, setTab] = useState('overview');
  const [topSearch, setTopSearch] = useState('');
  const [attrSearch, setAttrSearch] = useState('');
  useEffect(() => {
    let cancelled = false;
    setRecord(row);
    setError('');
    setLoading(false);
    if ('attributes' in row && 'resource' in row && 'events' in row && 'parent_span_id' in row && 'kind' in row) return;
    setLoading(true);
    loadSpan(row).then((value) => {
      if (!cancelled) setRecord(value ?? row);
    }).catch((err: unknown) => {
      if (!cancelled) setError(err instanceof Error ? err.message : String(err));
    }).finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => { cancelled = true; };
  }, [row, retry]);
  const copy = (text: string) => {
    void navigator.clipboard.writeText(text).then(() => toast.success(t('charts.copied'))).catch(() => toast.error(t('search.copyFailed')));
  };
  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="span-preview">
      {loading && <SpinArea />}
      {error && <ErrorHint message={error} onRetry={() => setRetry((value) => value + 1)} />}
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="overview">{t('search.drawerTabOverview')}</TabsTrigger>
          <TabsTrigger value="values">{t('search.drawerTabValues')}</TabsTrigger>
        </TabsList>
        <TabsContent value="overview" className="flex flex-col gap-4 pt-3">
          <OverviewTab kind="span" record={record} topSearch={topSearch} attrSearch={attrSearch}
            onTopSearch={setTopSearch} onAttrSearch={setAttrSearch} onCopy={copy}
            onFilterByValue={onFilterByValue} onFilterOnlyValue={onFilterOnlyValue} onTraceActivate={onTraceActivate} />
        </TabsContent>
        <TabsContent value="values" className="flex flex-col gap-3 pt-3">
          <ValuesTab record={record} onCopy={copy} />
        </TabsContent>
      </Tabs>
    </div>
  );
}
