import { panelEditorSettings } from './panelDefinition';
import { displaySql } from './displaySql';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  BarChart3,
  Bell,
  ChevronDown,
  CircleHelp,
  CircleMinus,
  CirclePlus,
  Code,
  Copy,
  Hash,
  LoaderCircle,
  LineChart,
  MoreHorizontal,
  Palette,
  PieChart,
  Play,
  Search,
  Sigma,
  ScatterChart,
  Table2,
} from 'lucide-react';
import dayjs from 'dayjs';
import type { EChartsOption } from 'echarts';
import { useTranslation } from 'react-i18next';
import type { EditorView } from '@uiw/react-codemirror';
import { searchSql } from '@/api/search';
import { SampleMatchedEvents } from './SampleMatchedEvents';
import { useChartWorkspace, useChartUrlState } from './ChartWorkspaceContext';
import { SaveChartModal } from './SaveChartModal';
import { NumberChart } from './NumberChart';
import { NumberSettingsDrawer } from './NumberSettingsDrawer';
import { DEFAULT_NUMBER_SETTINGS } from './numberTypes';
import type { NumberDisplaySettings, NumberFormat, NumberTrendQuery } from './numberTypes';
import { EventSearchExplorer } from './EventSearchExplorer';
import { PatternsExplorer } from './PatternsExplorer';
import { HeatmapExplorer } from './HeatmapExplorer';
import { PieExplorer } from './PieExplorer';
import { BarExplorer } from './BarExplorer';
import { TableExplorer } from './TableExplorer';
import { TimeSeriesSqlInstructions } from './TimeSeriesSqlInstructions';
import { canWriteRole, useAuthStore } from '@/store/auth';
import type { AnalyticsQueryResponse, SearchSqlParameters } from '@/api/types';
import {
  Button,
  Chart,
  DataTable,
  DropdownMenu,
  Empty,
  Input,
  Modal,
  Popconfirm,
  Popover,
  Select,
  Switch,
  Tag,
  toast,
  Tooltip,
  type TableColumn,
  TimeRangePicker,
  TIME_RANGE_PRESETS,
  type TimeRange,
} from '@/design-system';
import { useCssTheme } from '@/design-system/theme';
import { formatDateTime, formatDurationUs } from '@/utils/format';
import {
  buildChartSql,
  type AggFunc,
  type ChartSeries,
  type ChartType,
  type Granularity,
  type ChartSource,
  resolveGranularity,
} from './buildChartSql';
import type { ChartFormula, TableDisplaySettings } from '@/api/chartTypes';
import { ChartTable } from './ChartTable';
import { TableSettings } from './TableSettings';
import { FormulaError, parseFormula, seriesRef } from './formula';
import { visibleChartResult, explorerResult, chartTableResult } from './chartResult';
import { buildChartOption } from './chartOption';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { LuceneSearchHelp } from '@/pages/search/LuceneSearchHelp';
import { compileChartWhere } from './chartWhere';
import { MetricPicker } from './MetricPicker';

/** 图表类型页签. */
const CHART_TABS: { key: ChartType; icon: React.ReactNode; labelKey: string }[] = [
  { key: 'time_series', icon: <LineChart className="size-3.5" aria-hidden />, labelKey: 'charts.tabTimeSeries' },
  { key: 'table', icon: <Table2 className="size-3.5" aria-hidden />, labelKey: 'charts.tabTable' },
  { key: 'number', icon: <Hash className="size-3.5" aria-hidden />, labelKey: 'charts.tabNumber' },
  { key: 'bar', icon: <BarChart3 className="size-3.5" aria-hidden />, labelKey: 'charts.tabBar' },
  { key: 'pie', icon: <PieChart className="size-3.5" aria-hidden />, labelKey: 'charts.tabPie' },
  { key: 'search', icon: <Search className="size-3.5" aria-hidden />, labelKey: 'charts.tabSearch' },
  { key: 'heatmap', icon: <Palette className="size-3.5" aria-hidden />, labelKey: 'charts.tabHeatmap' },
  { key: 'patterns', icon: <ScatterChart className="size-3.5" aria-hidden />, labelKey: 'charts.tabPatterns' },
];

/** 共享查询状态的页签（同一 Builder 编辑器 + 页面级结果）；切换时必须按新类型重查. */
const SHARED_QUERY_TABS: ChartType[] = ['time_series', 'number'];

/** 聚合函数选项. */
const AGG_OPTIONS: { value: AggFunc; labelKey: string }[] = [
  { value: 'count', labelKey: 'charts.aggCount' },
  { value: 'avg', labelKey: 'charts.aggAvg' },
  { value: 'p95', labelKey: 'charts.aggP95' },
  { value: 'p50', labelKey: 'numberSources.median' },
  { value: 'p90', labelKey: 'numberSources.p90' },
  { value: 'p99', labelKey: 'numberSources.p99' },
  { value: 'any', labelKey: 'numberSources.any' },
  { value: 'custom', labelKey: 'numberSources.custom' },
  { value: 'sum', labelKey: 'charts.aggSum' },
  { value: 'min', labelKey: 'charts.aggMin' },
  { value: 'max', labelKey: 'charts.aggMax' },
  { value: 'count_distinct', labelKey: 'charts.aggCountDistinct' },
];

/** 时间粒度选项. */
const GRAN_OPTIONS: Exclude<Granularity, 'auto'>[] = ['1m', '5m', '15m', '30m', '1h', '6h', '1d'];

function initialRange(): TimeRange {
  const to = dayjs();
  return { from: to.subtract(1, 'hour').toISOString(), to: to.toISOString(), preset: '1h' };
}

interface QuerySnapshot {
  chartType: ChartType;
  domain: ChartSource;
  mode: 'builder' | 'sql';
  range: TimeRange;
  seriesList: ChartSeries[];
  groupBy: string[];
  granularity: Granularity;
  panelSql: string;
  numberTrend?: NumberTrendQuery;
  valueFilters: { field: string; value: unknown }[];
  formulas: ChartFormula[];
}


/** severity/status 列的语义 Tag 色调（其余值按原文展示）. */
const STATUS_TONES: Record<string, 'error' | 'warning' | 'success'> = {
  error: 'error',
  ERROR: 'error',
  WARN: 'warning',
  WARNING: 'warning',
  ok: 'success',
  INFO: 'success',
};

/** 列语义渲染（ts/severity/status/duration_us）. */
function renderCell(name: string, value: unknown): React.ReactNode {
  if (value === null || value === undefined) {
    return '-';
  }

  switch (name) {
    case 'ts':
      return formatDateTime(String(value));
    case 'severity':
    case 'status': {
      const text = String(value);
      const tone = STATUS_TONES[text];
      return tone ? <Tag tone={tone}>{text}</Tag> : text;
    }
    case 'duration_us':
      return formatDurationUs(Number(value));
    default:
      return typeof value === 'object' ? JSON.stringify(value) : String(value);
  }
}

export function ChartWorkspace() {
  const { t } = useTranslation();
  const cssTheme = useCssTheme();
  const workspace = useChartWorkspace();
  const initial = workspace.definition && SHARED_QUERY_TABS.includes(workspace.definition.chartType) ? workspace.definition : undefined;
  const saved = panelEditorSettings(workspace.initial);
  const searchViewRef = useRef<EditorView | null>(null);

  const [chartType, setChartType] = useState<ChartType>(workspace.urlTab ?? workspace.definition?.chartType ?? 'time_series');
  useEffect(() => { if (workspace.urlTab) setChartType(workspace.urlTab); }, [workspace.urlTab]);
  const [mode, setMode] = useState<'builder' | 'sql'>(initial?.mode ?? 'builder');
  const [domain, setDomain] = useState<ChartSource>(initial?.source ?? 'logs');
  const [chartName, setChartName] = useState(workspace.initial?.title ?? '');
  const [seriesList, setSeriesList] = useState<ChartSeries[]>(initial?.series.length ? initial.series : [
    { id: 's1', ref: 'A', aggFunc: 'count', field: '', where: '', alias: '' },
  ]);
  const [formulas, setFormulas] = useState<ChartFormula[]>(initial?.formulas ?? []);
  const [showInputSeries, setShowInputSeries] = useState(saved?.showInputSeries ?? true);
  const nextSeriesIndex = useRef(Math.max(initial?.series.length ?? 0, ...(initial?.series ?? []).map((item) => { let n = 0; for (const ch of item.ref ?? '') n = n * 26 + ch.charCodeAt(0) - 64; return n; }), 1));
  const [groupByInput, setGroupByInput] = useState(initial?.groupBy.join(', ') ?? '');
  const [tableOrderBy, setTableOrderBy] = useState('');
  const [tableSettings, setTableSettings] = useState<TableDisplaySettings>({ rowClick: true });
  const [granularity, setGranularity] = useState<Granularity>(initial?.granularity ?? 'auto');
  const [range, setRange] = useState<TimeRange>(() => workspace.range ?? workspace.urlState?.range ?? initialRange());
  const [snapshot, setSnapshot] = useState<QuerySnapshot | null>(null);
  const [displayType, setDisplayType] = useState<'line' | 'bar'>(saved?.display ?? 'line');
  const [valueFilters, setValueFilters] = useState<{ field: string; value: unknown }[]>(initial?.valueFilters ?? []);
  const [eventsError, setEventsError] = useState<string | null>(null);
  const [queryGeneration, setQueryGeneration] = useState(0);
  const requestId = useRef(0);
  const [showLegend, setShowLegend] = useState(workspace.initial?.showLegend ?? true);
  const [numberSettings, setNumberSettings] = useState<NumberDisplaySettings>(saved?.numberSettings ?? DEFAULT_NUMBER_SETTINGS);
  const [numberFormats, setNumberFormats] = useState<Record<string, NumberFormat>>(saved?.numberFormats ?? {});
  const [numberSettingsTarget, setNumberSettingsTarget] = useState<string | null>(null);
  const [sqlText, setSqlText] = useState(initial?.mode === 'sql' ? workspace.urlState?.sql ?? workspace.initial?.query?.sql ?? workspace.initial?.sql ?? '' : '');
  const sqlDrafts = useRef<Partial<Record<ChartSource, { sql: string; parameters: SearchSqlParameters }>>>({});
  const [generatedParameters, setGeneratedParameters] = useState<SearchSqlParameters>({});
  const [sqlParameters, setSqlParameters] = useState<SearchSqlParameters>(workspace.urlState?.parameters ?? workspace.initial?.query?.parameters ?? {});
  const [generated, setGenerated] = useState<string | null>(null);
  const [sqlError, setSqlError] = useState<string | null>(null);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  const [events, setEvents] = useState<AnalyticsQueryResponse | null>(null);
  const [eventsOpen, setEventsOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sqlOpen, setSqlOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const [sqlPreview, setSqlPreview] = useState<{ sql: string; parameters: SearchSqlParameters } | null>(null);
  const writable = canWriteRole(useAuthStore((state) => state.role));

  const activeRange = snapshot?.range ?? range;
  // 渲染形态始终跟随激活页签；snapshot 只记录最近一次执行（供缩放/下钻回填），不决定渲染类型
  const renderedType = chartType;
  const renderedSeries = snapshot?.seriesList ?? seriesList;
  const sampleDomain = snapshot?.domain ?? domain;

  useEffect(() => () => { requestId.current += 1; }, []);

  const groupBy = useMemo(
    () => groupByInput.split(',').map((col) => col.trim()).filter((col) => col.length > 0),
    [groupByInput],
  );

  useChartUrlState(SHARED_QUERY_TABS.includes(chartType), {
    editor: { version: 1, chartType, mode, source: domain, series: seriesList, formulas, groupBy, granularity, valueFilters },
    range, sql: sqlText, parameters: sqlParameters,
  });

  const updateSeries = (index: number, patch: Partial<ChartSeries>) => {
    setSeriesList((list) => list.map((item, i) => (i === index ? { ...item, ...patch } : item)));
  };

  const addSeries = (original?: ChartSeries) => {
    const ref = seriesRef(nextSeriesIndex.current++);
    setSeriesList((list) => [...list, { ...(original ?? { aggFunc: domain === 'metrics' || domain === 'iot' ? 'avg' : 'count', field: domain === 'iot' ? 'num_value' : domain === 'metrics' ? 'value' : '', where: '', alias: '', ...(domain === 'metrics' ? { metricName: '' } : {}) }), id: crypto.randomUUID(), ref }]);
  };
  const formulasSupported = mode === 'builder' && domain !== 'sessions' && !['search', 'patterns', 'heatmap'].includes(chartType);
  const formulaErrors = formulas.map((formula) => {
    try { parseFormula(formula.expression, seriesList.map((series, index) => series.ref ?? seriesRef(index))); return ''; }
    catch (err) { return err instanceof FormulaError ? t(`charts.${err.key}`, { ref: err.ref }) : String(err); }
  });

  const removeSeries = (index: number) => {
    setSeriesList((list) => list.filter((_, i) => i !== index));
  };

  const run = useCallback(async (selectedRange: TimeRange = range, targetType: ChartType = chartType) => {
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === selectedRange.preset);
    const to = preset ? dayjs() : dayjs(selectedRange.to);
    const from = preset ? to.subtract(preset.ms, 'millisecond') : dayjs(selectedRange.from);
    if (!from.isValid() || !to.isValid() || !from.isBefore(to)) {
      setSqlError(t('search.badTimeInput'));
      return;
    }
    const executedRange = { ...selectedRange, from: from.toISOString(), to: to.toISOString() };
    const rangeMs = to.diff(from);
    if (mode === 'builder' && domain !== 'sessions' && !['search', 'patterns', 'heatmap'].includes(targetType) && formulaErrors.some(Boolean)) { setSqlError(formulaErrors.find(Boolean)!); return; }
    const activeFormulas = mode === 'builder' && domain !== 'sessions' && !['search', 'patterns', 'heatmap'].includes(targetType) ? formulas : [];
    const parameters: SearchSqlParameters = mode === 'sql' ? { ...sqlParameters } : {};
    let filteredSeries = seriesList;
    let chartSql: string;
    let eventsSql: string | null = null;
    let panelSql: string;
    let numberTrend: NumberTrendQuery | undefined;
    if (mode === 'sql') {
      chartSql = sqlText;
      panelSql = chartSql;
    } else {
      if (targetType === 'heatmap' && groupBy.length === 0) {
        setSqlError(t('charts.heatmapNeedsGroupBy'));
        return;
      }

      try {
        if (domain === 'metrics' && seriesList.some((series) => series.metricName === '')) throw new Error(t('metricPicker.required'));
        filteredSeries = compileChartWhere(domain, seriesList, valueFilters, parameters);
        chartSql = buildChartSql({ domain, chartType: targetType, tableOrderBy, seriesList: filteredSeries, groupBy, granularity, rangeMs, formulas: activeFormulas, parameters });
        panelSql = granularity === 'auto' ? buildChartSql({ domain, chartType: targetType, tableOrderBy, seriesList: filteredSeries, groupBy, granularity, rangeMs, bucketParameter: true, formulas: activeFormulas, parameters }) : chartSql;
        if (targetType === 'number') {
          const trendParameters: SearchSqlParameters = { ...parameters };
          const sql = buildChartSql({ domain, chartType: 'time_series', seriesList: filteredSeries, groupBy: [], granularity: 'auto', rangeMs, bucketParameter: true, stableSeriesIds: true, formulas: activeFormulas, parameters: trendParameters });
          numberTrend = { sql, parameters: trendParameters };
        }
      } catch (err) {
        setSqlError(err instanceof FormulaError ? t(`charts.${err.key}`, { ref: err.ref }) : err instanceof Error ? err.message : String(err));
        return;
      }

      eventsSql = domain === 'metrics' ? null : buildChartSql({ domain, chartType: 'search', seriesList: filteredSeries, groupBy, granularity, rangeMs });
    }

    const generation = ++requestId.current;
    setRange(executedRange);
    setSnapshot({ chartType: targetType, domain, mode, range: executedRange, seriesList, groupBy, granularity, valueFilters, formulas: activeFormulas,
      panelSql, numberTrend });
    setQueryGeneration(generation);
    setResult(null);
    setEvents(null);
    setGenerated(chartSql);
    setGeneratedParameters(parameters);
    setSqlError(null);
    setError(null);
    setEventsError(null);
    setLoading(true);
    try {
      const rowLimit = targetType === 'number' ? 100 : 10_000;
      const [chartRes, eventsRes] = await Promise.allSettled([
        searchSql({ sql: chartSql, from: executedRange.from, to: executedRange.to, rowLimit, parameters }),
        eventsSql
          ? searchSql({ sql: eventsSql, from: executedRange.from, to: executedRange.to, rowLimit: 500, parameters })
          : Promise.resolve(null),
      ]);
      if (generation !== requestId.current) return;
      if (chartRes.status === 'fulfilled') {
        const value = chartRes.value;
        setResult(targetType === 'table' && mode === 'builder' ? { ...value, rows: value.rows.slice(0, 500), truncated: value.truncated || value.rows.length > 500 } : value);
      }
      else setError(chartRes.reason instanceof Error ? chartRes.reason.message : String(chartRes.reason));
      if (eventsRes.status === 'fulfilled') setEvents(eventsRes.value);
      else setEventsError(eventsRes.reason instanceof Error ? eventsRes.reason.message : String(eventsRes.reason));
    } finally {
      if (generation === requestId.current) setLoading(false);
    }
  }, [tableOrderBy, range, mode, sqlText, chartType, domain, seriesList, groupBy, granularity, valueFilters, sqlParameters, formulas, formulasSupported, formulaErrors, t]);

  // 首次进入自动运行（默认时序图 + 近 1 小时）
  useEffect(() => {
    if (workspace.mode !== 'panel' && !workspace.onApply && SHARED_QUERY_TABS.includes(chartType)) void run();
  }, []);

  // 按值过滤后自动重查（effect 在新 filters 落位后触发，捕获最新 run）
  const [filterTick, setFilterTick] = useState(0);
  useEffect(() => {
    if (filterTick > 0) void run();
  }, [filterTick]);

  const addValueFilter = (field: string, value: unknown, only = false, targetDomain: ChartSource = sampleDomain) => {
    if (targetDomain !== sampleDomain) {
      setMode('builder'); setDomain(targetDomain);
      setSeriesList([{ id: 's1', ref: 'A', aggFunc: 'count', field: '', where: '', alias: '' }]);
      setFormulas([]);
      setGroupByInput(''); setValueFilters([{ field, value }]);
      setFilterTick((tick) => tick + 1);
      return;
    }
    setMode('builder');
    setDomain(sampleDomain);
    if (snapshot) {
      setChartType(snapshot.chartType);
      setRange({ from: snapshot.range.from, to: snapshot.range.to });
      setSeriesList(snapshot.seriesList);
      setFormulas(snapshot.formulas);
      setGroupByInput(snapshot.groupBy.join(', '));
      setGranularity(snapshot.granularity);
    }
    setValueFilters((filters) => {
      const base = snapshot?.valueFilters ?? filters;
      return [...(only ? base.filter((filter) => filter.field !== field) : base), { field, value }];
    });
    setFilterTick((tick) => tick + 1);
  };


  const switchMode = (next: 'builder' | 'sql') => {
    if (next === mode) {
      return;
    }

    if (next === 'sql' && sqlText.length === 0 && !sqlDrafts.current[domain]) {
      try {
        const parameters: SearchSqlParameters = {};
        const series = compileChartWhere(domain, seriesList, valueFilters, parameters);
        const sql = buildChartSql({ domain, chartType, seriesList: series, groupBy, granularity, rangeMs: dayjs(range.to).diff(dayjs(range.from)), formulas, parameters });
        setSqlText(sql);
        setSqlParameters(parameters);
      } catch (err) {
        setSqlError(err instanceof Error ? err.message : String(err));
        return;
      }
    }
    if (next === 'builder') sqlDrafts.current[domain] = { sql: sqlText, parameters: { ...sqlParameters } };

    setMode(next);
  };

  const switchSource = (source: ChartSource) => {
    if (source === domain) return;
    if (mode === 'sql' || sqlText.length > 0 || sqlDrafts.current[domain]) {
      sqlDrafts.current[domain] = { sql: sqlText, parameters: { ...sqlParameters } };
    }
    const series: ChartSeries[] = [{ id: 's1', ref: 'A', aggFunc: source === 'metrics' || source === 'iot' ? 'avg' : 'count', field: source === 'iot' ? 'num_value' : source === 'metrics' ? 'value' : '', where: '', alias: '', ...(source === 'metrics' && mode === 'builder' ? { metricName: '' } : {}) }];
    const draft = sqlDrafts.current[source];
    setSqlText(draft?.sql ?? (mode === 'sql' ? buildChartSql({ domain: source, chartType, seriesList: series, groupBy: [], granularity, rangeMs: dayjs(range.to).diff(dayjs(range.from)) }) : ''));
    setSqlParameters({ ...draft?.parameters });
    setDomain(source);
    setSeriesList(series);
    setFormulas([]);
    setGroupByInput('');
    setValueFilters([]);
    // 切源后旧响应和结果失效；新来源只在运行时提交。
    requestId.current += 1;
    setQueryGeneration(requestId.current);
    setLoading(false);
    setResult(null); setEvents(null); setSnapshot(null);
    setGenerated(null); setGeneratedParameters({});
    setSqlError(null); setError(null); setEventsError(null);
    setSqlPreview(null); setSaveOpen(false);
  };

  /** 页签切换：共享查询页签清掉上一页签结果并按新类型立即重查，杜绝旧形态数据渲染进新页签；其余页签由各自探索器自行查询. */
  const switchChartType = (next: ChartType) => {
    if (next === chartType) {
      return;
    }

    setChartType(next);
    workspace.onTabChange?.(next);
    requestId.current += 1;
    setQueryGeneration(requestId.current);
    setLoading(false);
    setSaveOpen(false);
    if (!SHARED_QUERY_TABS.includes(next)) return;
    setResult(null);
    setEvents(null);
    setError(null);
    setEventsError(null);
    void run(range, next);
  };

  /** 通用查询结果 → 行对象数组. */
  const toRows = (res: AnalyticsQueryResponse | null) =>
    res ? res.rows.map((cells) => Object.fromEntries(res.columns.map((name, i) => [name, cells[i]]))) : [];

  const displayedResult = useMemo(() => result ? renderedType === 'table' && snapshot?.mode === 'builder' ? explorerResult(result, { chartType: 'table', source: snapshot.domain, series: snapshot.seriesList, range: snapshot.range, display: displayType, autoGranularity: false, tableSettings, tableGroupBy: snapshot.groupBy, formulas: snapshot.formulas, showInputSeries }) : visibleChartResult(result, !snapshot?.formulas.length || showInputSeries) : null, [result, snapshot, showInputSeries, renderedType, displayType, tableSettings]);
  const tabularResult = useMemo(() => displayedResult ? chartTableResult(displayedResult) : null, [displayedResult]);

  /** 图表配置（时序/柱状/饼图/热力图；无数据时空时间轴）. */
  const chartOption = useMemo<EChartsOption | null>(() => {
    if (!result || renderedType === 'number' || renderedType === 'table' || renderedType === 'search' || renderedType === 'patterns') {
      return null;
    }

    return buildChartOption(displayedResult ?? result, renderedType, activeRange, renderedSeries, showLegend, displayType, cssTheme.border);
  }, [result, displayedResult, renderedType, activeRange, renderedSeries, showLegend, displayType, cssTheme.border]);


  const tableRows = useMemo(() => toRows(tabularResult), [tabularResult]);
  const tableColumns = useMemo<TableColumn<Record<string, unknown>>[]>(() => {
    if (!result) {
      return [];
    }

    return (tabularResult?.columns ?? []).map((name) => ({
      title: name,
      dataIndex: name,
      width: name === 'ts_bucket' ? 170 : undefined,
      mono: true,
      ellipsis: true,
      render: (value: unknown) => renderCell(name, value),
    }));
  }, [result, tabularResult]);

  const copyToClipboard = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      toast.success(t('charts.copied'));
    } catch {
      toast.error(t('search.copyFailed'));
    }
  };

  const isTabular = renderedType === 'table' || renderedType === 'search';
  const notImplemented = renderedType === 'patterns';
  const timeSeriesSql = chartType === 'time_series' && mode === 'sql';

  /** SQL 预览下拉菜单（Builder/运行区各一处）. */
  const sqlActionsMenu = (align: 'start' | 'end' = 'start') => (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <Button variant="ghost" size="sm" testId={align === 'end' ? 'chart-actions' : 'chart-source-actions'} icon={<MoreHorizontal className="size-3.5" aria-hidden />} aria-label={t('charts.actions')}>{t('charts.actions')}</Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align={align}>
        {align === 'end' && writable && <DropdownMenu.Item disabled={!result || !generated || loading || !!error || notImplemented} icon={<Table2 className="size-3.5" />} onSelect={() => setSaveOpen(true)}>{t(workspace.onApply ? 'charts.applyPanel' : 'charts.saveToDashboard')}</DropdownMenu.Item>}
        <DropdownMenu.Item
          icon={<Code className="size-3.5" aria-hidden />}
          disabled={!generated}
          onSelect={() => generated && setSqlPreview({ sql: generated, parameters: { ...generatedParameters } })}
        >
          {t('charts.previewSql')}
        </DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  );

  const alertControl = () => (
    <Popover.Root>
      <Popover.Trigger asChild>
        <Button variant="ghost" icon={<Bell className="size-3.5" aria-hidden />}>{t('charts.addAlert')}</Button>
      </Popover.Trigger>
      <Popover.Content className="w-max max-w-64">
        <span className="text-xs">{t('charts.comingSoon')}</span>
      </Popover.Content>
    </Popover.Root>
  );

  const displaySettingsControl = () => chartType === 'number' ? (
    <Button icon={<Palette className="size-3.5" />} onClick={() => setNumberSettingsTarget('chart')}>{t('charts.displaySettings')}</Button>
  ) : (
    <Popover.Root>
      <Popover.Trigger asChild>
        <Button icon={<Palette className="size-3.5" />}>{t('charts.displaySettings')}</Button>
      </Popover.Trigger>
      <Popover.Content className="w-max">
        {chartType === 'table' ? <TableSettings value={tableSettings} columns={tabularResult?.columns ?? []} onChange={setTableSettings} /> : <div className="flex items-center gap-2">
          <span>{t('charts.showLegend')}</span>
          <Switch checked={showLegend} onChange={setShowLegend} aria-label={t('charts.showLegend')} />
        </div>}
      </Popover.Content>
    </Popover.Root>
  );

  return (
    <div className="flex h-full min-w-0 flex-col overflow-auto pb-6 [&>div]:shrink-0 font-mono text-[13px]">
      {/* 图表类型页签（手写 chip 行：激活项绿色描边，HyperDX 式强调） */}
      <div className="flex shrink-0 flex-wrap border-b border-border pt-1" role="tablist">
        {CHART_TABS.map((tab) => {
          const active = chartType === tab.key;
          return (
            <button
              key={tab.key}
              type="button"
              role="tab"
              aria-selected={active}
              onClick={() => switchChartType(tab.key)}
              className={
                active
                  ? 'cursor-pointer border-b-2 border-primary px-3 py-2.5 text-[13px] text-fg transition-colors'
                  : 'cursor-pointer border-b-2 border-transparent px-3 py-2.5 text-[13px] text-muted transition-colors hover:bg-surface-2 hover:text-fg'
              }
            >
              <span className="mr-1.5 inline-flex">{tab.icon}</span>
              {t(tab.labelKey)}
            </button>
          );
        })}
      </div>

      <TableExplorer active={chartType === 'table'} name={chartName} onNameChange={setChartName} />
      <BarExplorer active={chartType === 'bar'} name={chartName} onNameChange={setChartName} />
      <PieExplorer active={chartType === 'pie'} name={chartName} onNameChange={setChartName} />
      <HeatmapExplorer active={chartType === 'heatmap'} name={chartName} onNameChange={setChartName} />
      <EventSearchExplorer active={chartType === 'search'} name={chartName} onNameChange={setChartName} />
      <PatternsExplorer active={chartType === 'patterns'} name={chartName} onNameChange={setChartName} />
      <div data-testid="chart-aggregate-editor" className={chartType === 'bar' || chartType === 'table' || chartType === 'pie' || chartType === 'search' || chartType === 'heatmap' || chartType === 'patterns' ? 'hidden' : 'contents'}>
      {/* Chart Name + Builder/SQL 切换 */}
      <div className="flex flex-wrap items-center gap-2 border-b border-border/60 py-2.5">
        <span className="shrink-0 text-muted">{t('charts.chartName')}</span>
        <Input
          value={chartName}
          onChange={(event) => setChartName(event.target.value)}
          placeholder={t('charts.chartNamePlaceholder')}
          maxLength={80}
          className="min-w-[200px] flex-1 font-mono text-[13px]"
        />
        <div className="flex shrink-0 items-center" role="group" aria-label={t('charts.queryMode')}>
          <button
            type="button"
            onClick={() => switchMode('builder')}
            aria-pressed={mode === 'builder'}
            className={
              mode === 'builder'
                ? 'rounded-l-md border border-accent/60 bg-surface-2 px-3 py-1 text-accent transition-colors'
                : 'rounded-l-md border border-transparent px-3 py-1 text-muted transition-colors hover:bg-surface-2 hover:text-fg'
            }
          >
            {t('charts.builderMode')}
          </button>
          <button
            type="button"
            onClick={() => switchMode('sql')}
            aria-pressed={mode === 'sql'}
            className={
              mode === 'sql'
                ? 'rounded-r-md border border-accent/60 bg-surface-2 px-3 py-1 text-accent transition-colors'
                : 'rounded-r-md border border-transparent px-3 py-1 text-muted transition-colors hover:bg-surface-2 hover:text-fg'
            }
          >
            {t('charts.sqlMode')}
          </button>
        </div>
      </div>

      {/* Data Source */}
      <div className="flex flex-wrap items-center gap-2 border-b border-border/60 py-2.5">
        <span className="shrink-0 text-muted">{t('charts.dataSource')}</span>
        <div className="w-[180px] shrink-0">
          <Select
            value={domain}
            onValueChange={(value) => switchSource(value as ChartSource)}
            aria-label="domain"
            options={[
              { value: 'logs', label: <span className="flex items-center gap-1.5">☰ {t('search.sourceLogs')}</span> },
              { value: 'metrics', label: t('charts.sourceMetrics') },
              { value: 'sessions', label: t('charts.sourceSessions') },
              { value: 'iot', label: t('charts.sourceMqtt') },
              { value: 'traces', label: <span className="flex items-center gap-1.5">⇄ {t('search.sourceTraces')}</span> },
            ]}
          />
        </div>
        {sqlActionsMenu()}
        {timeSeriesSql && <div className="flex flex-wrap items-center gap-2">{alertControl()}{displaySettingsControl()}</div>}
      </div>

      {/* 序列行（Builder） */}
      {mode === 'builder' && seriesList.map((series, index) => (
        <div key={series.id} className="border-b border-border/60 py-2.5">
          <div className="mb-2 flex flex-wrap items-center justify-end gap-2">
            <Tag>{series.ref ?? seriesRef(index)}</Tag>
            <span className="text-muted">{t('charts.alias')}</span>
            <div className="w-44"><Input value={series.alias} onChange={(event) => updateSeries(index, { alias: event.target.value })} placeholder={t('charts.seriesAlias')} aria-label={`${t('charts.alias')} ${index + 1}`} maxLength={64} /></div>
            <Button variant="link" size="sm" icon={<Copy className="size-3.5" />} onClick={() => addSeries(series)}>{t('charts.duplicateSeries')}</Button>
            {chartType === 'number' && <Button variant="link" size="sm" icon={<Hash className="size-3.5" />} onClick={() => setNumberSettingsTarget(series.id)}>{t('numberChart.format')}</Button>}
          </div>
          <div className="flex flex-wrap items-center gap-2">
          <div className="w-[200px] shrink-0">
            <Select
              value={series.aggFunc}
              onValueChange={(value) => updateSeries(index, { aggFunc: value as AggFunc, columnExpression: true, metricDelta: false, field: value === 'count' ? '' : domain === 'metrics' ? 'value' : series.field })}
              aria-label="agg func"
              options={AGG_OPTIONS.map((option) => ({ value: option.value, label: t(option.labelKey) }))}
            />
          </div>
          {domain === 'metrics' && <MetricPicker key={series.id} index={index} value={series.metricName ?? ''} onChange={(metricName) => updateSeries(index, { metricName, metricFilters: [] })} range={range} onFilter={(field, value) => updateSeries(index, { metricFilters: [...(series.metricFilters ?? []), { field, value }] })} />}
          {series.aggFunc !== 'count' && (domain !== 'metrics' || series.aggFunc === 'custom') && <div className="w-56 min-w-0">
            <WhereSuggestInput domain={domain} syntax="sql" value={series.field} onChange={(field) => updateSeries(index, { field, columnExpression: true })} placeholder={t(series.aggFunc === 'custom' ? 'numberSources.customHint' : 'charts.barColumn')} ariaLabel={t('numberSources.column', { index: index + 1 })} fieldsOnly testId={'chart-column-' + index} from={null} to={null} denoise={false} onRun={() => void run()} />
          </div>}
          {domain === 'metrics' && !['count', 'count_distinct', 'custom'].includes(series.aggFunc) && <label className="flex items-center gap-2"><Switch checked={series.metricDelta ?? false} onChange={(metricDelta) => updateSeries(index, { metricDelta })} aria-label={t('numberSources.delta', { index: index + 1 })} />{t('numberSources.deltaLabel')}</label>}
          <span className="shrink-0 text-muted">{t('charts.where')}</span>
          <div className="w-[100px] shrink-0">
            <Select value={series.whereSyntax ?? 'sql'} aria-label={t('charts.whereSyntax', { index: index + 1 })} onValueChange={(syntax) => updateSeries(index, { whereSyntax: syntax as 'sql' | 'lucene' })} options={[{ value: 'sql', label: 'SQL' }, { value: 'lucene', label: 'Lucene' }]} />
          </div>
          <Tooltip content={t('charts.whereHint')} multiline>
            <Button variant="ghost" size="sm" icon={<CircleHelp className="size-3.5" aria-hidden />} aria-label={t('charts.whereHint')} />
          </Tooltip>
          <WhereSuggestInput
            domain={domain}
            syntax={series.whereSyntax ?? 'sql'}
            value={series.where}
            onChange={(where) => updateSeries(index, { where })}
            placeholder={t(series.whereSyntax === 'lucene' ? `search.luceneHelp.chart.placeholder.${domain}` : 'charts.wherePlaceholder')}
            className="min-w-[220px] flex-1 font-mono text-[13px]"
            testId={`chart-where-${index}`}
            from={range.from}
            to={range.to}
            denoise={false}
            onRun={() => void run()}
          />
          {index === 0 ? (chartType !== 'number' &&
            <>
              <span className="shrink-0 text-muted">{t('charts.groupBy')}</span>
              <Input
                value={groupByInput}
                onChange={(event) => setGroupByInput(event.target.value)}
                placeholder={t('charts.groupByPlaceholder')}
                className="min-w-[200px] flex-1 font-mono text-[13px]"
              />
            </>
          ) : (
            <Popconfirm
              title={t('charts.removeSeries')}
              okText={t('common.delete')}
              cancelText={t('common.cancel')}
              onConfirm={() => removeSeries(index)}
            >
              <Button variant="ghost" size="sm" danger icon={<CircleMinus className="size-3.5" aria-hidden />} aria-label={t('charts.removeSeries')} />
            </Popconfirm>
          )}
          </div>
          {series.whereSyntax === 'lucene' && <div className="mt-2"><LuceneSearchHelp source={domain} value={series.where} context="chart" /></div>}
          {domain === 'metrics' && <div className="mt-2 space-y-1 text-xs text-muted">
            {series.metricName === undefined && <p>{t('numberSources.legacyMetrics')}</p>}
            {series.metricDelta && <p>{t('numberSources.deltaHint')}</p>}
            {(series.metricFilters ?? []).map((filter, filterIndex) => <Button key={filterIndex} variant="link" size="sm" icon={<CircleMinus className="size-3.5" />} onClick={() => updateSeries(index, { metricFilters: series.metricFilters?.filter((_, i) => i !== filterIndex) })}>{filter.field}: {filter.value} · {t('numberSources.removeFilter')}</Button>)}
          </div>}
          {chartType === 'number' && domain === 'traces' && <p className="mt-2 text-xs text-muted">{t('numberSources.tracesHint')}</p>}
          {chartType === 'number' && domain === 'sessions' && <p className="mt-2 text-xs text-muted">{t('numberSources.sessionsHint')}</p>}
          {chartType === 'table' && <label className="mt-2 flex items-center gap-2 text-muted">{t('charts.tableHaving')}<Input value={series.having ?? ''} placeholder={t('charts.tableHavingHint')} onChange={(event) => updateSeries(index, { having: event.target.value })} /></label>}
        </div>
      ))}

      {formulasSupported && formulas.map((formula, index) => <div key={formula.id} className="border-b border-border py-3">
        <div className="flex items-center gap-2">
          <Sigma className="size-4 shrink-0 text-muted" />
          <Input value={formula.expression} aria-label={`${t('charts.formulaExpression')} ${index + 1}`} placeholder="A / (A + B) * 100" maxLength={512} invalid={Boolean(formulaErrors[index])} onChange={(event) => setFormulas((items) => items.map((item) => item.id === formula.id ? { ...item, expression: event.target.value } : item))} />
          {chartType === 'number' && <Button variant="link" size="sm" icon={<Hash className="size-3.5" />} onClick={() => setNumberSettingsTarget(formula.id)}>{t('numberChart.format')}</Button>}
          <Input value={formula.alias} aria-label={`${t('charts.formulaAlias')} ${index + 1}`} placeholder={t('charts.seriesAlias')} maxLength={64} onChange={(event) => setFormulas((items) => items.map((item) => item.id === formula.id ? { ...item, alias: event.target.value } : item))} />
          <Popconfirm title={t('charts.removeFormula')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => setFormulas((items) => items.filter((item) => item.id !== formula.id))}>
            <Button variant="link" danger size="sm" icon={<CircleMinus className="size-3.5" />}>{t('charts.removeFormula')}</Button>
          </Popconfirm>
        </div>
        {formulaErrors[index] && <p role="alert" className="mt-1 text-xs text-error">{formulaErrors[index]}</p>}
      </div>)}
      {!timeSeriesSql && !formulasSupported && formulas.length > 0 && <p className="py-2 text-xs text-muted">{t('charts.formulaUnavailable')}</p>}

      {/* SQL 模式编辑器 */}
      {timeSeriesSql && <TimeSeriesSqlInstructions source={domain} onCopy={(sql) => void copyToClipboard(sql)} />}
      {mode === 'sql' && (
        <div data-testid="chart-sql-workspace" className="mt-2.5 min-w-0 overflow-hidden rounded-md border border-border">
          {timeSeriesSql && <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border/60 bg-surface-2/50 px-3 py-1.5 text-xs text-muted">
            <span>{t('charts.sqlEditorTitle')}</span>
            <span>{t('charts.sqlRunShortcut')}</span>
          </div>}
          <SqlEditor value={sqlText} onChange={setSqlText} onRun={() => void run()} viewRef={searchViewRef} height={timeSeriesSql ? '260px' : undefined} />
        </div>
      )}

      {/* 操作行：添加序列/公式/告警 + 显示设置 */}
      {!timeSeriesSql && <div className="flex flex-wrap items-center justify-between gap-2 py-2.5">
        <div className="flex flex-wrap items-center gap-1">
          {mode === 'builder' && (
            <Button variant="ghost" icon={<CirclePlus className="size-3.5" aria-hidden />} onClick={() => addSeries()}>{t('charts.addSeries')}</Button>
          )}
          {domain !== 'sessions' && <Button variant="ghost" disabled={!formulasSupported || formulas.length >= 8} icon={<Sigma className="size-3.5" aria-hidden />} onClick={() => setFormulas((items) => [...items, { id: crypto.randomUUID(), expression: '', alias: '' }])}>{t('charts.addFormula')}</Button>}
          {formulas.length > 0 && <label className="flex items-center gap-2 px-2"><Switch checked={showInputSeries} onChange={setShowInputSeries} aria-label={t('charts.showInputSeries')} />{t('charts.showInputSeries')}</label>}
          {alertControl()}
        </div>
        {displaySettingsControl()}
      </div>}

      {mode === 'builder' && valueFilters.length > 0 && <div className="flex flex-wrap items-center gap-2 pb-2">
        {valueFilters.map((filter, index) => <Button key={index} size="sm" variant="secondary" onClick={() => { setValueFilters((filters) => filters.filter((_, i) => i !== index)); setFilterTick((tick) => tick + 1); }}>{filter.field}: {String(filter.value)} · {t('common.delete')}</Button>)}
      </div>}

      {chartType === 'table' && mode === 'builder' && <label className="flex items-center gap-2 pb-2"><span className="text-muted">ORDER BY</span><Input value={tableOrderBy} onChange={(event) => setTableOrderBy(event.target.value)} placeholder={t('charts.tableOrderHint')} aria-label="table-order-by" /></label>}

      {/* 时间范围 + 粒度 + 运行 */}
      <div className={timeSeriesSql ? 'flex flex-wrap items-center gap-2 py-2.5' : 'flex flex-wrap items-center justify-end gap-2 pb-2.5'}>
        {!workspace.range && <TimeRangePicker value={range} onChange={setRange} allowLive={false} showAbsoluteRange align={timeSeriesSql ? 'start' : 'end'} testId="chart-time-range" />}
        {!timeSeriesSql && chartType !== 'table' && chartType !== 'number' && <div className="w-[150px] shrink-0">
          <Select
            value={granularity}
            onValueChange={(value) => setGranularity(value as Granularity)}
            aria-label="granularity"
            options={[{ value: 'auto', label: `${t('charts.autoGranularity')} (${resolveGranularity('auto', dayjs(range.to).diff(dayjs(range.from)))})` }, ...GRAN_OPTIONS.map((gran) => ({ value: gran, label: gran }))]}
          />
        </div>}
        <Button variant="primary" testId="chart-run" disabled={loading} onClick={() => void run()} icon={loading ? <LoaderCircle className="size-3.5 animate-spin" /> : <Play className="size-3.5" />}>{t('charts.run')}</Button>
        {workspace.onApply && writable && <Button icon={<Table2 className="size-3.5" />} disabled={!result || loading || !!error} onClick={() => setSaveOpen(true)}>{t('charts.applyPanel')}</Button>}
        {sqlActionsMenu('end')}
      </div>

      {sqlError && (
        <div className="mb-2.5 flex items-center justify-between gap-2 rounded-md border border-warning/30 bg-warning-soft px-3 py-2 text-sm text-warning">
          <span className="min-w-0 break-words">{sqlError}</span>
          <button type="button" aria-label="dismiss" onClick={() => setSqlError(null)} className="cursor-pointer font-bold">
            ×
          </button>
        </div>
      )}
      {error && (
        <div className="mb-2.5 flex items-center justify-between gap-2 rounded-md border border-error/30 bg-error-soft px-3 py-2 text-sm text-error">
          <span className="min-w-0 break-words">{error}</span>
          <button type="button" aria-label="dismiss" onClick={() => setError(null)} className="cursor-pointer font-bold">
            ×
          </button>
        </div>
      )}

      {result?.truncated && <p role="status" className="pb-2 text-xs text-warning">{t('charts.resultTruncated')}</p>}
      {chartType === 'number' && <p className="pb-2 text-xs text-muted">{t('numberChart.rangeHint')}</p>}
      {/* 图表主体 */}
      {notImplemented ? (
        <Empty description={t('charts.notImplemented')} className="p-12" />
      ) : renderedType === 'number' ? (
        !error && <NumberChart executionKey={queryGeneration} result={displayedResult} settings={numberSettings} loading={loading} range={activeRange} trendQuery={snapshot?.numberTrend} items={[...renderedSeries.map((series) => ({ id: series.id, alias: series.alias.trim(), format: numberFormats[series.id] })), ...(snapshot?.formulas ?? []).map((formula) => ({ id: formula.id, alias: formula.alias.trim() || formula.expression, format: numberFormats[formula.id] }))]} />
      ) : renderedType === 'table' ? (
        <ChartTable result={tabularResult} settings={tableSettings} loading={loading} onRowClick={snapshot?.mode === 'builder' && snapshot.groupBy.length ? (cells) => {
          const filters = snapshot.groupBy.map((field) => ({ field, value: cells[tabularResult?.columns.indexOf(field) ?? -1] }));
          setDomain(snapshot.domain); setMode('builder'); setChartType('table'); setSeriesList(snapshot.seriesList); setFormulas(snapshot.formulas); setGroupByInput(snapshot.groupBy.join(', ')); setRange({ from: snapshot.range.from, to: snapshot.range.to }); setValueFilters([...snapshot.valueFilters, ...filters]); setEventsOpen(true); setFilterTick((tick) => tick + 1);
        } : undefined} />
      ) : isTabular ? (
        <DataTable<Record<string, unknown>>
          columns={tableColumns}
          dataSource={tableRows}
          loading={loading}
          rowKey={(_, index) => String(index)}
          pagination={{ pageSize: 50, showSizeChanger: false }}
          emptyText={<Empty description={t('charts.noDataHint')} className="py-8" />}
        />
      ) : (
        <div className="relative min-h-[360px] shrink-0 py-2">
          {renderedType === 'time_series' && <div className="mb-1 flex justify-end gap-1" role="group" aria-label={t('charts.displaySettings')}>
            <Button size="sm" variant={displayType === 'line' ? 'secondary' : 'ghost'} aria-pressed={displayType === 'line'} icon={<LineChart className="size-3.5" />} onClick={() => setDisplayType('line')}>{t('charts.lineDisplay')}</Button>
            <Button size="sm" variant={displayType === 'bar' ? 'secondary' : 'ghost'} aria-pressed={displayType === 'bar'} icon={<BarChart3 className="size-3.5" />} onClick={() => setDisplayType('bar')}>{t('charts.barDisplay')}</Button>
          </div>}
          {chartOption ? (
            <div className="h-[320px]"><Chart option={chartOption} minHeight={320} onEvents={{ datazoom: (event) => {
              type ZoomRange = { start?: number; end?: number; startValue?: number; endValue?: number };
              const zoom = event as unknown as ZoomRange & { batch?: ZoomRange[] };
              const selection = zoom.batch?.[0] ?? zoom;
              const start = dayjs(activeRange.from).valueOf();
              const span = dayjs(activeRange.to).valueOf() - start;
              const from = selection.startValue ?? (selection.start === undefined ? NaN : start + span * selection.start / 100);
              const to = selection.endValue ?? (selection.end === undefined ? NaN : start + span * selection.end / 100);
              if (!Number.isFinite(from) || !Number.isFinite(to) || from >= to || !snapshot) return;
              setMode(snapshot.mode);
              setDomain(snapshot.domain);
              setChartType(snapshot.chartType);
              setSeriesList(snapshot.seriesList);
              setFormulas(snapshot.formulas);
              setGroupByInput(snapshot.groupBy.join(', '));
              setGranularity(snapshot.granularity);
              setValueFilters(snapshot.valueFilters);
              if (snapshot.mode === 'sql') {
                setSqlText(generated ?? '');
                setSqlParameters(generatedParameters);
              }
              setRange({ from: new Date(from).toISOString(), to: new Date(to).toISOString() });
              setFilterTick((tick) => tick + 1);
            } }} /></div>
          ) : (
            <Empty description={loading ? t('common.loading') : t('charts.noDataHint')} className="pt-24" />
          )}
        </div>
      )}

      {SHARED_QUERY_TABS.includes(chartType) && sampleDomain !== 'metrics' && <SampleMatchedEvents open={eventsOpen} onOpenChange={setEventsOpen} source={sampleDomain} range={activeRange} mode={snapshot?.mode ?? mode} result={events} loading={loading} error={eventsError} generation={queryGeneration} onFilter={addValueFilter} />}

      {/* 折叠区：生成的 SQL */}
      <button
        type="button"
        aria-expanded={sqlOpen}
        aria-controls="chart-generated-sql"
        onClick={() => setSqlOpen((open) => !open)}
        className="flex w-full cursor-pointer items-center justify-between border-t border-border/60 px-0 py-3 text-left"
      >
        <span className="flex items-center gap-2">
          <Code className="size-3.5 text-subtle" aria-hidden />
          <span>{t('charts.generatedSql')}</span>
        </span>
        <ChevronDown className={cnCollapseChevron(sqlOpen)} aria-hidden />
      </button>
      {sqlOpen && <section id="chart-generated-sql" className="shrink-0 overflow-hidden rounded-sm border border-border">
        <div className="flex items-center justify-end border-b border-border/60 px-2 py-1"><Button size="sm" variant="secondary" icon={<Copy className="size-3.5" />} disabled={!generated} onClick={() => generated && void copyToClipboard(displaySql(generated, generatedParameters))}>{t('charts.copySql')}</Button></div>
        <SqlEditor value={generated ? displaySql(generated, generatedParameters) : '—'} readOnly height="auto" />
      </section>}
      {sqlPreview && <Modal open onOpenChange={(open) => { if (!open) setSqlPreview(null); }} title={t('charts.previewSql')} width={800} footer={
        <Button variant="primary" icon={<Copy className="size-3.5" />} onClick={() => void copyToClipboard(displaySql(sqlPreview.sql, sqlPreview.parameters))}>{t('charts.copySql')}</Button>
      }>
        <div className="overflow-hidden rounded-sm border border-border">
          <SqlEditor value={displaySql(sqlPreview.sql, sqlPreview.parameters)} readOnly height="320px" />
        </div>
      </Modal>}
      {numberSettingsTarget !== null && <NumberSettingsDrawer settings={numberSettings} seriesOnly={numberSettingsTarget !== 'chart'} override={numberFormats[numberSettingsTarget]} onClose={() => setNumberSettingsTarget(null)} onApply={(settings, override) => {
        if (numberSettingsTarget === 'chart') setNumberSettings(settings);
        else setNumberFormats((formats) => { const next = { ...formats }; if (override) next[numberSettingsTarget] = override; else delete next[numberSettingsTarget]; return next; });
      }} />}
      {(saveOpen || workspace.onApplyActionChange) && SHARED_QUERY_TABS.includes(chartType) && generated && snapshot && result && !loading && !error && !sqlError && <SaveChartModal editor={{ version: 1, compatibility: initial?.compatibility, mode: snapshot.mode, source: snapshot.domain, chartType: renderedType, series: snapshot.seriesList, formulas: snapshot.formulas, groupBy: snapshot.groupBy, granularity: snapshot.granularity, valueFilters: snapshot.valueFilters }} open={saveOpen} onOpenChange={setSaveOpen} name={chartName} numberSettings={numberSettings} numberFormats={numberFormats} numberTrend={snapshot.numberTrend} sql={snapshot.panelSql} autoGranularity={renderedType !== 'table' && snapshot.mode === 'builder' && snapshot.granularity === 'auto'} parameters={generatedParameters} tableSettings={tableSettings} tableGroupBy={snapshot.mode === 'builder' ? snapshot.groupBy : []} chartType={renderedType} source={sampleDomain} series={renderedSeries} formulas={snapshot.formulas} showInputSeries={showInputSeries} range={activeRange} display={displayType} showLegend={showLegend} />}

      </div>
    </div>
  );
}

/** 折叠区 chevron 旋转（展开时指向下→上翻转）. */
function cnCollapseChevron(open: boolean): string {
  return open ? 'size-3.5 shrink-0 text-subtle rotate-180 transition-transform' : 'size-3.5 shrink-0 text-subtle transition-transform';
}
