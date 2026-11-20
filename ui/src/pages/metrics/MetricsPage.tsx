import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { listMetricNames, queryMetricSeries } from '@/api/metrics';
import type { MetricSeriesItem } from '@/api/types';
import { Button, TimeSeriesChart, Page, Select } from '@/design-system';

/** 指标查询页：名称 + 标签过滤 + 区间聚合曲线. */
export function MetricsPage() {
  const { t } = useTranslation();
  const [names, setNames] = useState<string[]>([]);
  const [series, setSeries] = useState<MetricSeriesItem[]>([]);
  const [loading, setLoading] = useState(false);
  // 查询条件受控态（原 antd inline Form initialValues：aggregation=avg、stepSeconds=60）
  const [name, setName] = useState<string>('');
  const [labelKey, setLabelKey] = useState('');
  const [labelValue, setLabelValue] = useState('');
  const [aggregation, setAggregation] = useState('avg');
  const [stepSeconds, setStepSeconds] = useState('60');

  useEffect(() => {
    void listMetricNames().then(setNames).catch(() => undefined);
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const labels: Record<string, string> = {};
      if (labelKey && labelValue) {
        labels[labelKey] = labelValue;
      }

      const response = await queryMetricSeries({
        name,
        labels,
        aggregation: aggregation || 'avg',
        stepSeconds: Number(stepSeconds) || 60,
        // 缺省查最近 1 小时
        start: new Date(Date.now() - 60 * 60 * 1000).toISOString(),
        end: new Date().toISOString(),
      });
      setSeries(response.series);
    } finally {
      setLoading(false);
    }
  }, [name, labelKey, labelValue, aggregation, stepSeconds]);

  return (
    <Page
      extra={(
        <Button variant="primary" loading={loading} onClick={() => void load()}>
          {t('common.query')}
        </Button>
      )}
    >
      <div className="flex h-full flex-col gap-3">
        <div className="flex flex-wrap items-center gap-2 rounded-lg border border-border bg-surface px-4 py-3">
          <Select
            className="min-w-52"
            value={name}
            onValueChange={setName}
            placeholder={t('metrics.name')}
            aria-label={t('metrics.name')}
            options={names.map((item) => ({ value: item, label: item }))}
          />
          <input
            className="h-8 w-[130px] rounded-md border border-border bg-surface px-2.5 text-sm text-fg outline-none placeholder:text-faint focus:border-primary focus:ring-2 focus:ring-primary/20"
            placeholder="label key"
            value={labelKey}
            onChange={(event) => setLabelKey(event.target.value)}
          />
          <input
            className="h-8 w-[130px] rounded-md border border-border bg-surface px-2.5 text-sm text-fg outline-none placeholder:text-faint focus:border-primary focus:ring-2 focus:ring-primary/20"
            placeholder="label value"
            value={labelValue}
            onChange={(event) => setLabelValue(event.target.value)}
          />
          <Select
            className="w-[100px]"
            value={aggregation}
            onValueChange={setAggregation}
            aria-label="aggregation"
            options={['avg', 'sum', 'max', 'min', 'count', 'last', 'p95'].map((item) => ({ value: item, label: item }))}
          />
          <Select
            className="w-[110px]"
            value={stepSeconds}
            onValueChange={setStepSeconds}
            aria-label="step"
            options={[10, 30, 60, 300, 900, 3600].map((item) => ({ value: String(item), label: `${item}s` }))}
          />
        </div>
        <div className="min-h-[380px] flex-1 rounded-lg border border-border bg-surface p-3">
          <div className="h-full min-h-[360px]">
            <TimeSeriesChart series={series} zoom />
          </div>
        </div>
      </div>
    </Page>
  );
}
