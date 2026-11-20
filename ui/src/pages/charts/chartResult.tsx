import { tableMetricNames } from './tableQuery';
import type { ExplorerConfig, PanelVisualization } from '@/api/chartTypes';

interface Result { columns: string[]; rows: unknown[][] }
/** 输入序列始终参与计算，仅在展示层隐藏；内部标识不进入表格列. */
export function visibleChartResult<T extends Result>(result: T, showInputs = true): T {
  const marker = result.columns.indexOf('__formula');
  return { ...result, rows: marker >= 0 && !showInputs ? result.rows.filter((row) => row[marker] === true) : result.rows };
}

export function chartTableResult<T extends Result>(result: T): T {
  const indices = result.columns.map((_, index) => index).filter((index) => !result.columns[index].startsWith('__'));
  return { ...result, columns: indices.map((index) => result.columns[index]), rows: result.rows.map((row) => indices.map((index) => row[index])) };
}

export function ChartNumbers({ result, fallback = 'value' }: { result: Result; fallback?: string }) {
  const valueIndex = result.columns.indexOf('value');
  const nameIndex = result.columns.indexOf('series');
  return <div className="flex flex-wrap justify-center gap-6 p-4">
    {result.rows.map((row, index) => {
      const value = row[valueIndex];
      return <div key={index} className="text-center"><div className="font-mono text-xs text-muted">{nameIndex >= 0 ? String(row[nameIndex]) : fallback}</div><div className="font-mono text-4xl font-semibold">{value == null || !Number.isFinite(Number(value)) ? '-' : Number(value).toLocaleString()}</div></div>;
    })}
    {result.rows.length === 0 && <span className="font-mono text-4xl">-</span>}
  </div>;
}

export function panelResult<T extends Result>(result: T, config: PanelVisualization): T {
  const visible = visibleChartResult(result, config.showInputSeries !== false);
  const hidden = new Set(config.hiddenColumns ?? []);
  const indices = visible.columns.map((_, index) => index).filter((index) => !hidden.has(visible.columns[index]));
  return { ...visible, columns: indices.map((index) => visible.columns[index]), rows: visible.rows.map((row) => indices.map((index) => row[index])) };
}

export function explorerResult<T extends Result>(result: T, config: ExplorerConfig): T {
  if (config.chartType === 'table' && config.tableSettings && config.formulas?.length && config.showInputSeries === false) {
    const names = tableMetricNames({ seriesList: config.series, formulas: config.formulas, groupBy: config.tableGroupBy ?? [] });
    const hidden = new Set(names.slice(0, config.series.length));
    const indices = result.columns.map((_, index) => index).filter((index) => !hidden.has(result.columns[index]));
    return { ...result, columns: indices.map((index) => result.columns[index]), rows: result.rows.map((row) => indices.map((index) => row[index])) };
  }
  return visibleChartResult(result, !config.formulas?.length || config.showInputSeries !== false);
}
