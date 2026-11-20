import { createContext, useContext, useEffect } from 'react';
import type { ChartUrlState } from './chartUrlState';
import type { PanelConfig } from '@/api/types';
import type { ChartEditorDefinition } from '@/api/chartTypes';
import type { TimeRange } from '@/design-system';

export interface ChartWorkspaceValue {
  mode?: 'standalone' | 'panel';
  initial?: PanelConfig;
  definition?: ChartEditorDefinition;
  range?: TimeRange;
  onApply?: (panel: PanelConfig) => void;
  onApplyActionChange?: (action: (() => void) | null) => void;
  urlState?: ChartUrlState;
  urlTab?: import('@/api/chartTypes').ChartType;
  onTabChange?: (tab: import('@/api/chartTypes').ChartType) => void;
  onUrlStateChange?: (state: ChartUrlState) => void;
}

export const ChartWorkspaceContext = createContext<ChartWorkspaceValue>({});
export const useChartWorkspace = () => useContext(ChartWorkspaceContext);

/** 仅激活页签上报草稿；序列化依赖避免查询结果刷新时重复写地址. */
export function useChartUrlState(active: boolean, state: ChartUrlState) {
  const { mode, onUrlStateChange } = useChartWorkspace();
  const serialized = JSON.stringify(state);
  useEffect(() => {
    if (active && mode !== 'panel') onUrlStateChange?.(JSON.parse(serialized) as ChartUrlState);
  }, [active, mode, serialized, onUrlStateChange]);
}
