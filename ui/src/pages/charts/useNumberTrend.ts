import { useEffect, useState } from 'react';
import { searchSql } from '@/api/search';
import type { NumberResult, NumberTrendQuery } from './numberTypes';
import { resolveGranularity } from './buildChartSql';

const intervals = { '1m': '1 minute', '5m': '5 minutes', '15m': '15 minutes', '30m': '30 minutes', '1h': '1 hour', '6h': '6 hours', '1d': '1 day' };

/** 执行代次与区间共同确定查询，格式及颜色变更不重新请求. */
export function useNumberTrend(enabled: boolean, query: NumberTrendQuery | undefined, range: { from: string; to: string } | undefined, executionKey?: unknown) {
  const [state, setState] = useState<{ data: NumberResult | null; error: boolean; loading: boolean }>({ data: null, error: false, loading: false });
  const sql = query?.sql;
  const parameters = JSON.stringify(query?.parameters ?? {});
  const from = range?.from;
  const to = range?.to;
  useEffect(() => {
    let active = true;
    setState({ data: null, error: false, loading: Boolean(enabled && sql && from && to) });
    if (enabled && sql && from && to) {
      void searchSql({ sql, parameters: { ...JSON.parse(parameters), chart_bucket: intervals[resolveGranularity('auto', Date.parse(to) - Date.parse(from))] }, from, to, rowLimit: 10000 })
        .then((data) => { if (active) setState({ data, error: false, loading: false }); })
        .catch(() => { if (active) setState({ data: null, error: true, loading: false }); });
    }
    return () => { active = false; };
  }, [enabled, sql, parameters, from, to, executionKey]);
  return state;
}
