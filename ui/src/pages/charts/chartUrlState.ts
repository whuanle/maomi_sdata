import dayjs from 'dayjs';
import { validHeatmapConfig } from './heatmap';
import type { ChartEditorDefinition, ChartType, HeatmapConfig } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';

export interface ChartUrlState {
  editor: ChartEditorDefinition;
  range: TimeRange;
  sql?: string;
  parameters?: SearchSqlParameters;
  seriesLimit?: number;
  heatmap?: HeatmapConfig;
}

export const CHART_URL_TABS: ChartType[] = ['time_series', 'table', 'number', 'bar', 'pie', 'search', 'heatmap', 'patterns'];
const sources = ['logs', 'traces', 'metrics', 'sessions', 'iot'];
const aggregations = ['count', 'avg', 'sum', 'min', 'max', 'count_distinct', 'p50', 'p90', 'p95', 'p99', 'any', 'custom'];
const granularities = ['auto', '1m', '5m', '15m', '30m', '1h', '6h', '1d'];
const operators = ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'contains', 'exists', 'missing'];
type ObjectValue = Record<string, unknown>;
const object = (value: unknown): value is ObjectValue => typeof value === 'object' && value !== null && !Array.isArray(value);
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every((item) => typeof item === 'string');
const optionalString = (value: unknown) => value === undefined || typeof value === 'string';
const optionalBoolean = (value: unknown) => value === undefined || typeof value === 'boolean';
const syntax = (value: unknown) => value === undefined || value === 'lucene' || value === 'sql';
const scalar = (value: unknown) => value === null || ['string', 'boolean'].includes(typeof value) || typeof value === 'number' && Number.isFinite(value);
const filters = (value: unknown) => value === undefined || Array.isArray(value) && value.every((item) => object(item) && typeof item.field === 'string' && scalar(item.value) && optionalBoolean(item.exists));
const positiveInteger = (value: unknown, max: number) => typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= max;

function validSeries(value: unknown): boolean {
  if (!object(value) || typeof value.id !== 'string' || !aggregations.includes(String(value.aggFunc)) ||
    !['field', 'where', 'alias'].every((key) => typeof value[key] === 'string') || !optionalString(value.ref) || !syntax(value.whereSyntax) ||
    !optionalString(value.having) || !optionalString(value.metricName) || !optionalBoolean(value.metricDelta) || !optionalBoolean(value.columnExpression)) return false;
  if (value.metricFilters !== undefined && (!Array.isArray(value.metricFilters) || !value.metricFilters.every((item) => object(item) && typeof item.field === 'string' && typeof item.value === 'string'))) return false;
  if (value.filters !== undefined && (!Array.isArray(value.filters) || !value.filters.every((item) => object(item) && typeof item.id === 'string' && typeof item.field === 'string' && operators.includes(String(item.operator)) && typeof item.value === 'string'))) return false;
  return value.threshold === undefined || object(value.threshold) && ['gt', 'gte', 'lt', 'lte'].includes(String(value.threshold.operator)) && typeof value.threshold.value === 'string';
}

function validEditor(value: unknown, tab: ChartType): value is ChartEditorDefinition {
  if (!object(value) || value.version !== 1 || value.chartType !== tab || !sources.includes(String(value.source)) ||
    !['builder', 'sql'].includes(String(value.mode)) || !granularities.includes(String(value.granularity)) ||
    !Array.isArray(value.series) || !value.series.every(validSeries) || !strings(value.groupBy) ||
    !Array.isArray(value.formulas) || !value.formulas.every((item) => object(item) && ['id', 'expression', 'alias'].every((key) => typeof item[key] === 'string')) ||
    !filters(value.valueFilters) || !syntax(value.syntax) || !['where', 'patternExpression', 'orderBy'].every((key) => optionalString(value[key]))) return false;
  if (value.tableSeries !== undefined && (!Array.isArray(value.tableSeries) || !value.tableSeries.every(validSeries))) return false;
  if (value.tableQuery !== undefined && (!object(value.tableQuery) || !positiveInteger(value.tableQuery.topN, 10000) ||
    !Array.isArray(value.tableQuery.sort) || !value.tableQuery.sort.every((item) => object(item) && typeof item.columnId === 'string' && ['asc', 'desc'].includes(String(item.direction))))) return false;
  if (value.eventSearch !== undefined && (!object(value.eventSearch) || value.eventSearch.domain !== value.source ||
    !['fields', 'where', 'orderBy'].every((key) => typeof value.eventSearch === 'object' && value.eventSearch !== null && typeof (value.eventSearch as ObjectValue)[key] === 'string') ||
    !['lucene', 'sql'].includes(String(value.eventSearch.syntax)) || !Array.isArray(value.eventSearch.filters) || !filters(value.eventSearch.filters))) return false;
  if (tab === 'heatmap' && !['traces', 'metrics'].includes(String(value.source))) return false;
  if (value.mode === 'builder' && ['time_series', 'number', 'bar', 'pie', 'table'].includes(tab) && !value.series.length) return false;
  return tab !== 'search' || value.eventSearch !== undefined;
}

function defaultRange(tab: ChartType): TimeRange {
  const preset = tab === 'table' ? '1d' : '1h';
  const ms = TIME_RANGE_PRESETS.find((item) => item.key === preset)!.ms;
  const to = dayjs();
  return { from: to.subtract(ms, 'millisecond').toISOString(), to: to.toISOString(), preset };
}

/** URL 中的查询定义整体校验，避免只恢复一半条件；相对时间在打开链接时重新锚定. */
export function parseChartUrlState(params: URLSearchParams): { tab: ChartType; state?: ChartUrlState } {
  const rawTab = params.get('tab');
  const tab = CHART_URL_TABS.includes(rawTab as ChartType) ? rawTab as ChartType : 'time_series';
  try {
    const raw: unknown = JSON.parse(params.get('state') ?? 'null');
    if (!object(raw) || raw.version !== 1 || !validEditor(raw.editor, tab) || !optionalString(raw.sql)) return { tab };
    if (raw.parameters !== undefined && (!object(raw.parameters) || !Object.entries(raw.parameters).every(([key, value]) => /^[a-zA-Z_][a-zA-Z0-9_]*$/.test(key) && scalar(value)))) return { tab };
    if (raw.seriesLimit !== undefined && !positiveInteger(raw.seriesLimit, 10000)) return { tab };
    if (raw.heatmap !== undefined && (!object(raw.heatmap) || !validHeatmapConfig(raw.heatmap as unknown as HeatmapConfig))) return { tab };
    let range = defaultRange(tab);
    if (!object(raw.range)) return { tab };
    const preset = raw.range.preset;
    if (typeof preset === 'string' && TIME_RANGE_PRESETS.some((item) => item.key === preset)) {
      const to = dayjs();
      const ms = TIME_RANGE_PRESETS.find((item) => item.key === preset)!.ms;
      range = { from: to.subtract(ms, 'millisecond').toISOString(), to: to.toISOString(), preset };
    } else {
      if (typeof raw.range.from !== 'string' || typeof raw.range.to !== 'string') return { tab };
      const from = dayjs(raw.range.from), to = dayjs(raw.range.to);
      if (!from.isValid() || !to.isValid() || !from.isBefore(to)) return { tab };
      range = { from: from.toISOString(), to: to.toISOString() };
    }
    return { tab, state: { editor: raw.editor, range, sql: raw.sql as string | undefined, parameters: raw.parameters as SearchSqlParameters | undefined, seriesLimit: raw.seriesLimit as number | undefined, heatmap: raw.heatmap as HeatmapConfig | undefined } };
  } catch { return { tab }; }
}

export function serializeChartUrlState(state: ChartUrlState): Record<string, string> {
  const range = state.range.preset && TIME_RANGE_PRESETS.some((item) => item.key === state.range.preset)
    ? { preset: state.range.preset } : { from: state.range.from, to: state.range.to };
  return { tab: state.editor.chartType, state: JSON.stringify({ version: 1, ...state, range }) };
}
