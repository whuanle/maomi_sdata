import { useId, useMemo, useState } from 'react';
import { ChevronDown, ListOrdered } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { DataTable, Drawer, Empty, Tag } from '@/design-system';
import type { ChartSource } from '@/api/chartTypes';
import type { AnalyticsQueryResponse } from '@/api/types';
import { ResultsTable } from '@/pages/search/ResultsTable';
import { LogDetailDrawer } from '@/pages/search/LogDetailDrawer';
import { TraceDetailDrawer } from '@/pages/search/TraceDetailDrawer';
import { ReplayView } from '@/pages/rum/RumPage';
import { formatDateTime, formatDurationUs } from '@/utils/format';
import { DOMAIN_COLUMNS } from './buildChartSql';

interface Props {
  source: ChartSource;
  range: { from: string; to: string };
  mode: 'builder' | 'sql';
  result: AnalyticsQueryResponse | null;
  loading: boolean;
  error?: string | null;
  generation: number;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  onFilter: (field: string, value: unknown, only?: boolean, source?: ChartSource) => void;
}

/** 展示和详情共用，查询与过滤编译留在 Explorer 执行边界. */
export function SampleMatchedEvents(props: Props) {
  const { t } = useTranslation();
  const id = useId();
  const [localOpen, setLocalOpen] = useState(false);
  const open = props.open ?? localOpen;
  const setOpen = props.onOpenChange ?? setLocalOpen;
  return <>
    <button type="button" aria-expanded={open} aria-controls={id} onClick={() => setOpen(!open)} className="mt-3.5 flex w-full cursor-pointer items-center justify-between border-t border-border py-3 text-left">
      <span className="flex items-center gap-2"><ListOrdered className="size-3.5 text-subtle" /><span>{t('charts.sampleEvents')}</span>{props.result && <Tag>{props.result.rows.length}</Tag>}</span>
      <ChevronDown className={open ? 'size-3.5 rotate-180 text-subtle' : 'size-3.5 text-subtle'} />
    </button>
    <SampleContent key={`${props.generation}:${props.source}:${props.mode}`} {...props} open={open} id={id} />
  </>;
}

function SampleContent({ source, range, mode, result, loading, error, generation, onFilter, open, id }: Props & { open: boolean; id: string }) {
  const { t } = useTranslation();
  const [detail, setDetail] = useState<Record<string, unknown> | null>(null);
  const [trace, setTrace] = useState<Record<string, unknown> | null>(null);
  const [other, setOther] = useState<Record<string, unknown> | null>(null);
  const rows = useMemo(() => result?.rows.map((row) => Object.fromEntries(result.columns.map((column, index) => [column, row[index]]))) ?? [], [result]);
  const columns = DOMAIN_COLUMNS[source].filter((column) => result?.columns.includes(column));
  const filter = (field: string, value: unknown, only = false, target = source) => {
    setDetail(null); setTrace(null); setOther(null);
    onFilter(field, value, only, target);
  };
  const render = (name: string, value: unknown) => {
    if (value == null) return '-';
    if (name === 'ts') return formatDateTime(String(value));
    if (name === 'duration_us') return formatDurationUs(Number(value));
    return typeof value === 'object' ? JSON.stringify(value) : String(value);
  };
  return <>
    {open && <section id={id} aria-label={t('charts.sampleEvents')}>
      {mode === 'sql' ? <p className="py-3 text-muted">{t('charts.sqlSamplesUnavailable')}</p> : error ? <p role="alert" className="py-3 text-error">{error}</p> : loading ? <p role="status" className="py-3 text-muted">{t('common.loading')}</p> : <>
        <p className="pb-2 text-xs text-muted">{t('charts.sampleScope')}{result?.truncated ? ' · ' + t('charts.samplesTruncated') : ''}</p>
        <div className="h-[420px] overflow-hidden border-y border-border/60">
          {source === 'logs' || source === 'traces'
            ? <ResultsTable domain={source} columns={columns} rows={rows} queryGeneration={generation} empty={<Empty description={t('charts.noDataHint')} className="py-8" />} onTraceOpen={(trace_id) => setTrace({ trace_id })} onRowOpen={(row) => source === 'traces' ? setTrace(row) : setDetail(row)} onFilterByValue={filter} />
            : <DataTable columns={columns.map((name) => ({ key: name, dataIndex: name, title: name, render: (value: unknown) => render(name, value) }))} dataSource={rows} onRow={(row) => ({ onClick: () => setOther(row), className: 'cursor-pointer hover:bg-surface-2' })} rowKey={(_, index) => String(index)} pagination={{ pageSize: 20 }} />}
        </div>
      </>}
    </section>}
    {detail && <LogDetailDrawer domain="logs" row={detail} onClose={() => setDetail(null)} onFilterByValue={filter} onFilterOnlyValue={(field, value) => filter(field, value, true)} onTraceActivate={(trace_id) => setTrace({ trace_id })} />}
    {trace && <TraceDetailDrawer row={trace} from={range.from} to={range.to} onClose={() => setTrace(null)} onFilterByValue={(field, value, domain) => filter(field, value, false, domain ?? 'traces')} onFilterOnlyValue={(field, value, domain) => filter(field, value, true, domain ?? 'traces')} />}
    {other && <Drawer open onOpenChange={(value) => { if (!value) setOther(null); }} title={t(source === 'iot' ? 'charts.sourceMqtt' : source === 'sessions' ? 'charts.sourceSessions' : 'charts.sourceMetrics')}>
      <pre className="mb-4 overflow-auto font-mono text-xs">{JSON.stringify(other, null, 2)}</pre>
      {source === 'sessions' && <ReplayView projectKey={String(other.project_key ?? '')} sessionId={String(other.session_id ?? '')} range={range} />}
    </Drawer>}
  </>;
}
