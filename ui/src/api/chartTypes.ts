/** Chart Explorer 与持久化面板共用的定义. */
export type ChartSource = 'logs' | 'metrics' | 'sessions' | 'traces' | 'iot';
export type ChartType = 'time_series' | 'table' | 'number' | 'bar' | 'pie' | 'search' | 'heatmap' | 'patterns';
export type AggFunc = 'count' | 'avg' | 'sum' | 'min' | 'max' | 'count_distinct' | 'p50' | 'p90' | 'p95' | 'p99' | 'any' | 'custom';
export type Granularity = 'auto' | '1m' | '5m' | '15m' | '30m' | '1h' | '6h' | '1d';

export type ChartOutputFormat = 'number' | 'currency' | 'percentage' | 'duration' | 'time' | 'data' | 'data_rate' | 'throughput';

export interface ChartValueFormat {
  output: ChartOutputFormat;
  decimals: number;
  thousands: boolean;
  compact: boolean;
}

export interface BarDisplaySettings {
  seriesLimit?: number;
  format: ChartValueFormat;
}

export interface ChartSeries {
  id: string;
  ref?: string;
  aggFunc: AggFunc;
  field: string;
  where: string;
  whereSyntax?: 'sql' | 'lucene';
  alias: string;
  format?: ChartValueFormat;
  having?: string;
  metricName?: string;
  metricDelta?: boolean;
  columnExpression?: boolean;
  metricFilters?: { field: string; value: string }[];
}

export interface ChartFormula {
  id: string;
  expression: string;
  alias: string;
}

export interface HeatmapConfig {
  field: 'duration_us' | 'value';
  bins: number;
  scale?: 'linear' | 'log';
  colorScale?: 'linear' | 'log';
}

export interface TableSort { columnId: string; direction: 'asc' | 'desc' }
export interface TableColumnIdentity { id: string; name: string; kind: 'group' | 'metric' }
export interface TableQuerySettings { topN: number; sort: TableSort[] }

export interface TableDisplaySettings {
  striped?: boolean;
  stickyHeader?: boolean;
  decimals?: number;
  format?: 'number' | 'percent' | 'bytes';
  color?: 'default' | 'primary' | 'success' | 'warning';
  rowClick?: boolean;
  columns?: Record<string, { decimals?: number; format?: 'number' | 'percent' | 'bytes'; color?: 'default' | 'primary' | 'success' | 'warning' }>;
}

export type PieDisplaySettings = BarDisplaySettings;

export type NumberColor = 'default' | 'primary' | 'accent' | 'success' | 'warning' | 'error';
export type NumberFormat = ChartValueFormat & { unit?: string };
export interface NumberDisplaySettings {
  format: NumberFormat;
  color: NumberColor;
  rules: { id: string; operator: 'gt' | 'gte' | 'lt' | 'lte' | 'eq'; value: number; color: NumberColor }[];
  background: 'none' | 'line' | 'area' | 'bar';
  backgroundColor?: NumberColor;
}
export interface NumberTrendQuery {
  sql: string;
  parameters: import('./types').SearchSqlParameters;
}

/** 原始编辑状态与执行 SQL 分开保存，禁止从 SQL 猜测 Builder. */
export interface ChartEditorDefinition {
  version: 1;
  compatibility?: boolean;
  mode: 'builder' | 'sql';
  source: ChartSource;
  chartType: ChartType;
  series: ChartSeries[];
  formulas: ChartFormula[];
  groupBy: string[];
  granularity: Granularity;
  valueFilters?: { field: string; value: unknown }[];
  tableSeries?: (ChartSeries & {
    filters?: { id: string; field: string; operator: 'eq' | 'neq' | 'gt' | 'gte' | 'lt' | 'lte' | 'contains' | 'exists' | 'missing'; value: string }[];
    threshold?: { operator: 'gt' | 'gte' | 'lt' | 'lte'; value: string };
  })[];
  tableQuery?: TableQuerySettings;
  orderBy?: string;
  eventSearch?: { domain: ChartSource; fields: string; syntax: 'lucene' | 'sql'; where: string; orderBy: string; filters: { field: string; value: unknown; exists?: boolean }[] };
  where?: string;
  syntax?: 'lucene' | 'sql';
  patternExpression?: string;
  barSettings?: BarDisplaySettings;
  barGroupBy?: string[];
  heatmap?: HeatmapConfig;
}

/** 面板运行时展示协议，不包含查询构建器或公式表达式. */
export interface PanelVisualization {
  version: 1;
  kind: ChartType;
  display: 'line' | 'bar';
  range: { from: string; to: string };
  outputs: { id: string; alias: string; label?: string; format?: ChartValueFormat }[];
  showInputSeries?: boolean;
  hiddenColumns?: string[];
  numberSettings?: NumberDisplaySettings;
  numberFormats?: Record<string, NumberFormat>;
  barSettings?: BarDisplaySettings;
  pieSettings?: PieDisplaySettings;
  tableSettings?: TableDisplaySettings;
  tableColumns?: TableColumnIdentity[];
  heatmap?: HeatmapConfig;
}

export interface ExplorerConfig {
  editor?: ChartEditorDefinition;
  numberSettings?: NumberDisplaySettings;
  numberFormats?: Record<string, NumberFormat>;
  numberTrend?: NumberTrendQuery;
  pieSettings?: PieDisplaySettings;
  tableSettings?: TableDisplaySettings;
  tableGroupBy?: string[];
  tableQuery?: TableQuerySettings;
  tableColumns?: TableColumnIdentity[];
  heatmap?: HeatmapConfig;
  chartType: string;
  source: string;
  series: ChartSeries[];
  range: { from: string; to: string };
  display: 'line' | 'bar';
  autoGranularity: boolean;
  version?: number;
  formulas?: ChartFormula[];
  showInputSeries?: boolean;
  barSettings?: BarDisplaySettings;
  barGroupBy?: string[];
  orderBy?: string;
}
