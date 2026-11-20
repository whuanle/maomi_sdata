import { useCallback, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { ChartWorkspace } from './ChartWorkspace';
import { ChartWorkspaceContext } from './ChartWorkspaceContext';
import { parseChartUrlState, serializeChartUrlState, type ChartUrlState } from './chartUrlState';

/** HyperDX 风格图表构建页（Builder 生成 SQL / SQL 直写，经 /api/analytics/search 执行）. */
export function ChartExplorerPage() {
  const [params, setParams] = useSearchParams();
  const search = params.toString();
  const writtenSearch = useRef(search);
  const [restored, setRestored] = useState(() => ({ ...parseChartUrlState(params), revision: 0 }));

  // 自己的 replace 不重建编辑器；外部导航必须先恢复，再进行首次查询。
  if (search !== writtenSearch.current) {
    writtenSearch.current = search;
    setRestored((previous) => ({ ...parseChartUrlState(params), revision: previous.revision + 1 }));
  }
  const onUrlStateChange = useCallback((state: ChartUrlState) => {
    const next = new URLSearchParams(writtenSearch.current);
    for (const [key, value] of Object.entries(serializeChartUrlState(state))) next.set(key, value);
    const serialized = next.toString();
    if (serialized === writtenSearch.current) return;
    writtenSearch.current = serialized;
    setParams(next, { replace: true });
  }, [setParams]);

  const state = restored.state;
  return <ChartWorkspaceContext.Provider value={{ mode: 'standalone',
    definition: state?.editor, urlState: state, urlTab: restored.tab, onUrlStateChange,
  }}>
    <ChartWorkspace key={restored.revision} />
  </ChartWorkspaceContext.Provider>;
}
