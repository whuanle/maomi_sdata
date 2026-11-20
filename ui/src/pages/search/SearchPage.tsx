import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { EChartsOption } from 'echarts';
import {
  Bell,
  Braces,
  CodeXml,
  Copy,
  Download,
  FileSearch,
  Play,
  Save,
  Table2,
  Workflow,
} from 'lucide-react';
import dayjs, { type Dayjs } from 'dayjs';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { deleteSavedQuery, getSavedQuery, listSavedQueries, saveQuery } from '@/api/analytics';
import { HISTOGRAM_GROUP_FIELD, SEARCH_FIELDS, searchSql } from '@/api/search';
import type {
  AnalyticsQueryResponse,
  SavedQueryItem,
  SavedSearchDefinition,
  SearchDomain,
  SearchSqlParameters,
} from '@/api/types';
import {
  Button,
  Chart,
  Empty,
  Input,
  Modal,
  Page,
  Popconfirm,
  Popover,
  Select,
  Tag,
  TimeRangePicker,
  TIME_RANGE_PRESETS,
  toast,
  Tooltip,
  type ChartEventMap,
  type TimeRange,
} from '@/design-system';
import { useCssTheme as readCssTheme } from '@/design-system/theme';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { formatDateTime } from '@/utils/format';
import { LuceneCompileError } from './lucene';
import { effectiveSearch } from './effectiveSearch';
import { editSearchExpression, searchExpression } from './filterExpression';
import { SearchAlertsModal } from './SearchAlertsModal';
import { jsonFieldExpression, parseJsonField, quoteFieldAlias, splitFieldList } from './jsonFields';
import { SelectColumnsPopover, SortEditorPopover } from './ColumnPicker';
import { WhereSuggestInput } from './WhereSuggestInput';
import { LuceneSearchHelp } from './LuceneSearchHelp';
import { ResultsTable } from './ResultsTable';
import { LogDetailDrawer } from './LogDetailDrawer';
import { SearchSidePanel } from './SearchSidePanel';
import type { AnalysisMode } from './SearchSidePanel';
import { applyValueOnly, type SearchFilter } from './filters';
import {
  DEFAULT_FIELDS,
  DEFAULT_ORDER_BY,
  LIMIT_OPTIONS,
  REFRESH_INTERVAL_OPTIONS,
  extractWhereFragment,
  parseSearchUrlState,
  serializeSearchUrlState,
} from './searchUrlState';
import type { SearchUrlState } from './searchUrlState';
import { SqlEditor } from './SqlEditor';
import { TraceDetailDrawer } from './TraceDetailDrawer';
import { downloadCsv } from './csv';

const SEVERITY_ORDER = ['FATAL', 'ERROR', 'WARN', 'INFO', 'DEBUG', 'TRACE'];
const STATUS_ORDER = ['error', 'ok', 'unset'];

/** 分组堆叠配色（severity/status 语义色，echarts 数据系列色允许字面量）. */
const GROUP_COLORS: Record<string, string> = {
  FATAL: '#cf1322',
  ERROR: '#cf1322',
  WARN: '#fa8c16',
  INFO: '#52c41a',
  DEBUG: '#1677ff',
  TRACE: '#8c8c8c',
  error: '#cf1322',
  ok: '#52c41a',
  unset: '#8c8c8c',
};

/** 直方图整刻度桶步长（毫秒，升序；ClickStack 式高密度分桶）. */
const BUCKET_STEPS_MS = [
  1_000, 5_000, 10_000, 15_000, 30_000,
  60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000,
  3_600_000, 7_200_000, 10_800_000, 21_600_000, 43_200_000, 86_400_000,
];

/** 按时间窗自适应直方图步长（取使桶数 ≤ 120 的最小整刻度，60~120 桶保证密度）. */
function pickBucketStepMs(rangeMs: number): number {
  const span = Math.max(60_000, rangeMs);
  for (const step of BUCKET_STEPS_MS) {
    if (span / step <= 120) {
      return step;
    }
  }

  return BUCKET_STEPS_MS[BUCKET_STEPS_MS.length - 1];
}

/** 服务端全量聚合直方图数据（零填充全桶轴 + 分组堆叠计数 + 范围内匹配总数）. */
interface HistogramData {
  bucketStarts: number[];
  counts: Map<string, number[]>;
  groups: string[];
  total: number;
}

/** 聚合响应 → 直方图数据（服务端已按整刻度对齐分桶，这里补齐空桶零值保持时间位置正确）. */
function buildHistogram(response: AnalyticsQueryResponse, from: Dayjs, to: Dayjs, stepMs: number, groupOrder: string[]): HistogramData {
  const bucketCol = response.columns.indexOf('bucket');
  const groupCol = response.columns.indexOf('grp');
  const cntCol = response.columns.indexOf('cnt');
  const start = Math.floor(from.valueOf() / stepMs) * stepMs;
  const length = Math.max(1, Math.ceil((to.valueOf() - start) / stepMs));
  const bucketStarts = Array.from({ length }, (_, index) => start + index * stepMs);
  const indexByBucket = new Map(bucketStarts.map((bucket, index) => [bucket, index]));
  const counts = new Map<string, number[]>();
  let total = 0;
  for (const cells of response.rows) {
    const bucket = Math.floor(Number(cells[bucketCol]) / stepMs) * stepMs;
    const index = indexByBucket.get(bucket);
    if (index === undefined) {
      continue;
    }

    const group = String(cells[groupCol] ?? '-');
    const series = counts.get(group) ?? new Array<number>(bucketStarts.length).fill(0);
    series[index] += Number(cells[cntCol]) || 0;
    counts.set(group, series);
    total += Number(cells[cntCol]) || 0;
  }

  const groups = [...counts.keys()].sort((a, b) => groupOrder.indexOf(a) - groupOrder.indexOf(b));
  return { bucketStarts, counts, groups, total };
}

/** 构建产物：可执行搜索 SQL + 命名参数（$p0..$pN 绑定值，服务端 DuckDBParameter）+ WHERE 主体（供直方图聚合复用）. */
interface BuiltQuery {
  sql: string;
  parameters: SearchSqlParameters;
  /** WHERE 主体（含 $start/$end 与其余条件，不含 ORDER BY/LIMIT；两种语法共用，供直方图服务端聚合复用）. */
  whereClause?: string;
  visibleFields?: string[];
}

/** SearchPage props. */
export interface SearchPageProps {
  /** 已保存搜索 id（/saved-searches/:savedId 路由注入；传入时打开即应用快照，保存即更新该条目）. */
  savedId?: number | string;
}

/** 统一搜索页：日志/链路单一入口（HyperDX 式布局：SELECT/ORDER BY 关键字条 + 属性搜索栏 + 细柱直方图 + 就地展开结果表）. */
export function SearchPage({ savedId }: SearchPageProps) {
  const { t } = useTranslation();
  const cssTheme = readCssTheme();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const canWrite = useAuthStore((state) => canWriteRole(state.role));

  const source = (searchParams.get('source') === 'traces' ? 'traces' : 'logs') as SearchDomain;
  // URL 条件恢复：仅首挂载解析一次，带参地址打开即恢复全部查询条件
  const [initialState] = useState<SearchUrlState>(() => parseSearchUrlState(searchParams));
  const [syntax, setSyntax] = useState<'lucene' | 'sql'>(initialState.syntax);
  const [whereText, setWhereText] = useState(initialState.whereText);
  const [fieldsText, setFieldsText] = useState<string>(initialState.fieldsText);
  const [orderByText, setOrderByText] = useState<string>(initialState.orderByText);
  const [analysisMode, setAnalysisMode] = useState<AnalysisMode>('table');
  const [viewMode, setViewMode] = useState<'table' | 'json'>('table');
  const [denoise, setDenoise] = useState(initialState.denoise);
  const [filters, setFilters] = useState<SearchFilter[]>(initialState.filters);
  // 输入展示完整条件，持久化与执行仍沿用草稿/筛选双轨，避免旧链接迁移及重复叠加。
  const [luceneEdit, setLuceneEdit] = useState<{ text: string; where: string; filters: SearchFilter[]; denoise: boolean; source: SearchDomain } | null>(null);
  const activeDenoise = denoise && source === 'logs';
  const luceneExpression = luceneEdit && luceneEdit.where === whereText && luceneEdit.filters === filters
    && luceneEdit.denoise === activeDenoise && luceneEdit.source === source
    ? luceneEdit.text : searchExpression(whereText, filters, activeDenoise);
  const onLuceneChange = (value: string) => {
    const edited = editSearchExpression(value, filters, activeDenoise);
    setLuceneEdit({ text: value, ...edited, source });
    setWhereText(edited.where);
    setFilters(edited.filters);
    if (source === 'logs') setDenoise(edited.denoise);
  };
  // SQL 模式手写 WHERE 片段（行2；行1 已定义 SELECT/ORDER BY，与 Lucene 表达式同位不同语法）
  const [sqlWhere, setSqlWhere] = useState<string>(initialState.sqlWhere);
  const [timeRangeValue, setTimeRangeValue] = useState<TimeRange>(initialState.timeRange);
  const [limit, setLimit] = useState<number>(initialState.limit);
  // 定时刷新间隔（秒；相对范围/Live Tail 下轮询重查，自定义区间自动停用）
  const [refreshIntervalSec, setRefreshIntervalSec] = useState<number>(initialState.refreshIntervalSec);
  const [result, setResult] = useState<AnalyticsQueryResponse | null>(null);
  // 最近一次实际执行的 SQL（生成 SQL 弹层展示：查询 SQL + 直方图 SQL）
  const [lastSql, setLastSql] = useState<string | null>(null);
  const [lastHistogramSql, setLastHistogramSql] = useState<string | null>(null);
  const [generatedSqlOpen, setGeneratedSqlOpen] = useState(false);
  // 服务端全量聚合直方图（构建产物含 WHERE 主体即有；null 时回退为返回行客户端分桶）
  const [histogram, setHistogram] = useState<HistogramData | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searchedRange, setSearchedRange] = useState<[Dayjs, Dayjs] | null>(null);
  // 属性发现锚定显式查询，轮询滑窗不清空筛选树和输入建议。
  const [facetRange, setFacetRange] = useState<[Dayjs, Dayjs] | null>(null);
  const [visibleColumns, setVisibleColumns] = useState<string[]>([]);
  // 详情抽屉目标行（ClickStack 式侧栏；行点击打开，与行首箭头的就地展开区分）
  const [detailRow, setDetailRow] = useState<Record<string, unknown> | null>(null);
  const [detailRange, setDetailRange] = useState<[string, string] | null>(null);
  // 查询序号（连续切换范围时丢弃过期响应）
  const runSeqRef = useRef(0);
  const pendingExecutionRef = useRef<number | null>(null);
  const [queryGeneration, setQueryGeneration] = useState(0);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadMoreError, setLoadMoreError] = useState<string | null>(null);
  const pageRef = useRef<{
    seq: number; sql: string; parameters: SearchSqlParameters;
    from: string; to: string; rowLimit: number; offset: number; hasMore: boolean;
  } | null>(null);
  const pendingPageRef = useRef(false);

  const resetPages = useCallback(() => {
    const seq = ++runSeqRef.current;
    pendingExecutionRef.current = null;
    pageRef.current = null;
    pendingPageRef.current = false;
    setQueryGeneration(seq);
    setLoadingMore(false);
    setLoadMoreError(null);
    setLoading(false);
    return seq;
  }, []);

  useEffect(() => () => { runSeqRef.current += 1; pageRef.current = null; }, []);
  // 保存的搜索恢复状态后的延迟重查标记（effect 落地后以恢复完的状态重查一次）
  const pendingRunRef = useRef(false);

  // 保存的搜索（单一入口：Save search 弹层 = 保存当前 + 列表）
  const [savedList, setSavedList] = useState<SavedQueryItem[]>([]);
  const [saveOpen, setSaveOpen] = useState(false);
  const [alertsOpen, setAlertsOpen] = useState(false);
  const [saveName, setSaveName] = useState('');
  const [saving, setSaving] = useState(false);
  // 已保存搜索上下文（savedId 路由注入）：非空时保存动作 = 覆盖更新该条目
  const [activeSaved, setActiveSaved] = useState<SavedQueryItem | null>(null);

  // 切域统一覆盖选择器、深链与浏览器回退：两种语法均重置列/排序并关闭旧详情。
  // 显式跨域过滤携带目标条件；普通切域清空过滤器，手写 WHERE 保留由用户调整。
  const pendingDomainFilter = useRef<SearchFilter[] | null>(null);
  const [adaptedFromSource, setAdaptedFromSource] = useState<SearchDomain | null>(null);
  if (adaptedFromSource !== source) {
    const fromDomain = adaptedFromSource;
    setAdaptedFromSource(source);
    if (fromDomain !== null && savedId === undefined) {
      // 切域是上下文变更：详情抽屉目标行属于旧域，一并关闭
      setDetailRow(null);
      setFieldsText(DEFAULT_FIELDS[source]);
      setOrderByText(DEFAULT_ORDER_BY[source]);
      setFilters(pendingDomainFilter.current ?? []);
      pendingDomainFilter.current = null;
    }
  }

  const isCustomRange = timeRangeValue.preset === undefined;

  const rangeMs = useMemo(() => {
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === timeRangeValue.preset);
    if (preset) return preset.ms;
    return Math.max(60_000, dayjs(timeRangeValue.to).diff(dayjs(timeRangeValue.from)));
  }, [timeRangeValue]);

  /** 当前查询时间窗（预设为实时滑窗，Live Tail 轮询下逐次前移）. */
  const currentTimeRange = useCallback((): [Dayjs, Dayjs] => {
    if (isCustomRange) {
      return [dayjs(timeRangeValue.from), dayjs(timeRangeValue.to)];
    }
    return [dayjs().subtract(rangeMs), dayjs()];
  }, [isCustomRange, timeRangeValue, rangeMs]);

  const loadSavedList = useCallback(async () => {
    try {
      const list = await listSavedQueries({ kind: 'search', page: 1, pageSize: 50 });
      setSavedList(list.data);
    } catch {
      // 保存列表加载失败不阻塞搜索主流程
    }
  }, []);

  /** 行1 关键字条（SELECT 字段/ORDER BY）白名单校验与组装：物理列直取；JSON 字段（点号字面 key / 括号嵌套路径）
   * 经 parseJsonField 校验后编译为 json_valid 防护 + 绑定路径的提取表达式，以显示名作列别名（结果表列名即字段引用）。
   * HyperDX 式两种语法同构共用（overrides 供 traceId 深链等显式状态入口）；parameters 为查询级绑定字典（路径 $jpN 落此处）. */
  const buildProjection = (overrides?: { fields?: string; orderBy?: string }, parameters: SearchSqlParameters = {}): { fields: string[]; selectItems: string[]; orderSql: string } => {
    const schema = SEARCH_FIELDS[source];
    const fields = splitFieldList(overrides?.fields ?? fieldsText);
    if (fields.length === 0) {
      throw new LuceneCompileError(t('search.fieldsEmpty'));
    }

    const jsonExprs = new Map<string, string>();
    for (const field of fields) {
      if (schema.some((meta) => meta.field === field)) {
        continue;
      }

      const jsonPath = parseJsonField(field, source);
      if (!jsonPath) {
        throw new LuceneCompileError(`${t('search.unknownField')} ${field}（${t('search.allowedFields')}: ${schema.map((meta) => meta.field).join(', ')}，${t('search.jsonPathFormat')}）`);
      }

      jsonExprs.set(field, jsonFieldExpression(jsonPath, parameters));
    }

    const selectItems = fields.map((field) => {
      const expr = jsonExprs.get(field);
      return expr ? `${expr} AS ${quoteFieldAlias(field)}` : field;
    });

    const orderParts = splitFieldList(overrides?.orderBy ?? orderByText);
    if (orderParts.length === 0) {
      throw new LuceneCompileError(t('search.fieldsEmpty'));
    }

    const ordered = new Set<string>();
    const orderSql = orderParts.map((part) => {
      // 尾随 asc/desc 剥离（JSON 显示名可含空格，不可按空白取首段）
      const match = part.match(/^(.+?)(?:\s+(asc|desc))?$/i);
      if (!match) {
        throw new LuceneCompileError(`${t('search.badOrderBy')} ${part}`);
      }

      const field = match[1];
      ordered.add(field);
      let orderExpr = field;
      if (!schema.some((meta) => meta.field === field)) {
        const jsonPath = parseJsonField(field, source);
        if (!jsonPath) {
          throw new LuceneCompileError(`${t('search.unknownField')} ${field}（${t('search.allowedFields')}: ${schema.map((meta) => meta.field).join(', ')}，${t('search.jsonPathFormat')}）`);
        }

        orderExpr = jsonExprs.get(field) ?? jsonFieldExpression(jsonPath, parameters);
      }

      return `${orderExpr} ${match[2] ? match[2].toUpperCase() : 'ASC'}`;
    }).join(', ');

    // tiebreaker 只用物理列（JSON 表达式逐行提取成本高，不参与兜底排序）
    const tieBreakers = [...(source === 'logs' ? ['id'] : []), ...schema.map((meta) => meta.field)]
      .filter((field) => !ordered.has(field));
    return { fields, selectItems, orderSql: [orderSql, ...tieBreakers.map((field) => `${field} ASC`)].join(', ') };
  };

  /** 完整查询组装：时间窗 WHERE 主体 + 行1 SELECT/ORDER BY + 行数上限. */
  const assembleQuery = (projection: { fields: string[]; selectItems: string[]; orderSql: string }, conditions: string[], parameters: SearchSqlParameters): BuiltQuery => {
    const whereClause = `ts >= $start AND ts < $end${conditions.length > 0 ? ` AND ${conditions.join(' AND ')}` : ''}`;
    return {
      sql: `SELECT ${[...new Set([...projection.selectItems, ...(source === 'traces' ? ['ts', 'service', 'trace_id', 'span_id'] : [])])].join(', ')}\nFROM {{${source}}}\nWHERE ${whereClause}\nORDER BY ${projection.orderSql}\nLIMIT CAST($page_limit AS BIGINT) OFFSET CAST($page_offset AS BIGINT)`,
      parameters,
      whereClause,
      visibleFields: projection.fields,
    };
  };

  /** Lucene 模式查询构建：表达式编译为参数化 WHERE 片段 + 过滤器/降噪并入. */
  const buildLuceneQuery = (overrides?: { fields?: string; orderBy?: string; whereText?: string; filters?: SearchFilter[] }): BuiltQuery => {
    const parameters: SearchSqlParameters = {};
    const { fields, selectItems, orderSql } = buildProjection(overrides, parameters);
    const effective = effectiveSearch({ domain: source, mode: 'lucene', where: overrides?.whereText ?? whereText,
      filters: overrides?.filters ?? filters, denoise, rangeMs }, parameters);
    return assembleQuery({ fields, selectItems, orderSql }, effective.condition ? [effective.condition] : [], parameters);
  };

  /** SQL 模式查询构建：行2 手写 WHERE 片段原样嵌入（经 /analytics/search 只读单语句通道校验执行）+ 行1/过滤器/降噪与 Lucene 同源. */
  const buildSqlQuery = (overrides?: { fields?: string; orderBy?: string }): BuiltQuery => {
    const parameters: SearchSqlParameters = {};
    const { fields, selectItems, orderSql } = buildProjection(overrides, parameters);
    const effective = effectiveSearch({ domain: source, mode: 'sql', where: sqlWhere, filters, denoise, rangeMs }, parameters);
    return assembleQuery({ fields, selectItems, orderSql }, effective.condition ? [effective.condition] : [], parameters);
  };

  const execute = useCallback(
    async (built: BuiltQuery, range: [Dayjs, Dayjs], rowLimit: number, background = false) => {
      if (background && pendingExecutionRef.current !== null) return;
      // 过期响应丢弃：连续切换时间范围/快速重查时只落最后一次请求的结果
      const seq = resetPages();
      pendingExecutionRef.current = seq;
      setLoading(true);
      setError(null);
      if (!background) {
        setSearchedRange(range);
        setFacetRange(range);
      }
      try {
        const parameters = built.parameters;
        const page = { seq, sql: built.sql, parameters, from: range[0].toISOString(), to: range[1].toISOString(), rowLimit, offset: 0, hasMore: false };
        pageRef.current = page;
        // 直方图聚合（两种语法共用）：与行查询同 WHERE，服务端全量分桶，计数不受行数上限影响；
        // 桶步长为内部计算的整刻度常量，经 $bucket_ms 参数绑定。
        // 注意：JSON 数字绑定被 DuckDB.NET 推断为 DOUBLE，`//` 对 DOUBLE 是精确除法而非整除，
        // 必须显式 floor() 再 CAST，否则桶不对齐（每行一桶，响应行数随日志量暴涨）
        const stepMs = pickBucketStepMs(range[1].valueOf() - range[0].valueOf());
        const histogramParameters = { ...(parameters ?? {}), bucket_ms: stepMs };
        const histogramRequest = built.whereClause
          ? {
              sql: [
                'SELECT CAST(floor(epoch_ms(ts) / $bucket_ms) * $bucket_ms AS BIGINT) AS bucket,',
                `       ${HISTOGRAM_GROUP_FIELD[source]} AS grp,`,
                '       count(*) AS cnt',
                `FROM {{${source}}}`,
                `WHERE ${built.whereClause}`,
                'GROUP BY 1, 2',
                'ORDER BY 1 ASC',
              ].join('\n'),
              parameters: histogramParameters,
            }
          : null;
        setLastSql(built.sql);
        setLastHistogramSql(histogramRequest ? histogramRequest.sql : null);
        const [response, histogramResponse] = await Promise.all([
          searchSql({ sql: page.sql, from: page.from, to: page.to, rowLimit, parameters: { ...parameters, page_limit: rowLimit + 1, page_offset: 0 } }),
          histogramRequest
            ? searchSql({ sql: histogramRequest.sql, from: range[0].toISOString(), to: range[1].toISOString(), rowLimit: 10_000, parameters: histogramParameters }).catch(() => null)
            : Promise.resolve(null),
        ]);
        if (seq !== runSeqRef.current) {
          return;
        }

        page.offset = response.rows.length;
        page.hasMore = response.truncated && response.rows.length > 0;
        setSearchedRange(range);
        setFacetRange((previous) => previous ?? range);
        setResult({ ...response, truncated: page.hasMore });
        setVisibleColumns(built.visibleFields ?? response.columns);
        setHistogram(histogramRequest && histogramResponse
          ? buildHistogram(histogramResponse, range[0], range[1], stepMs, source === 'logs' ? SEVERITY_ORDER : STATUS_ORDER)
          : null);
      } catch (err) {
        if (seq !== runSeqRef.current) {
          return;
        }

        if (!background) {
          setResult(null);
          setHistogram(null);
        }
        setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (seq === runSeqRef.current) {
          pendingExecutionRef.current = null;
          setLoading(false);
        }
      }
    },
    [source, resetPages],
  );

  const loadMore = useCallback(async () => {
    const page = pageRef.current;
    if (!page || !page.hasMore || pendingPageRef.current) return;
    pendingPageRef.current = true;
    setLoadingMore(true);
    setLoadMoreError(null);
    try {
      const response = await searchSql({
        sql: page.sql, from: page.from, to: page.to, rowLimit: page.rowLimit,
        parameters: { ...page.parameters, page_limit: page.rowLimit + 1, page_offset: page.offset },
      });
      if (page.seq !== runSeqRef.current) return;
      page.offset += response.rows.length;
      page.hasMore = response.truncated && response.rows.length > 0;
      setResult((previous) => previous && page.seq === runSeqRef.current ? {
        ...previous,
        rows: [...previous.rows, ...response.rows.map((cells) => previous.columns.map((name) => cells[response.columns.indexOf(name)]))],
        truncated: page.hasMore,
        elapsedMs: previous.elapsedMs + response.elapsedMs,
      } : previous);
    } catch (err) {
      if (page.seq === runSeqRef.current) setLoadMoreError(err instanceof Error ? err.message : String(err));
    } finally {
      if (page.seq === runSeqRef.current) {
        pendingPageRef.current = false;
        setLoadingMore(false);
      }
    }
  }, []);

  /** 当前状态运行（时间范围实时计算，Live Tail 轮询下为滑窗）. */
  const runCurrent = (overrides?: { fields?: string; orderBy?: string }, background = false) => {
    if (background && pendingExecutionRef.current !== null) return;
    try {
      const built = syntax === 'lucene' ? buildLuceneQuery(overrides) : buildSqlQuery(overrides);
      void execute(built, currentTimeRange(), limit, background);
    } catch (err) {
      if (!background) {
        resetPages();
        setResult(null);
        setHistogram(null);
      }
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  /** 以显式时间窗运行（直方图点击缩放等已计算好范围的入口）. */
  const runWithRange = (range: [Dayjs, Dayjs]) => {
    try {
      const built = syntax === 'lucene' ? buildLuceneQuery() : buildSqlQuery();
      void execute(built, range, limit);
    } catch (err) {
      resetPages();
      setResult(null);
      setHistogram(null);
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  /**
   * 时间范围选择变更：立即以新窗口重查（选完即查，不等 10s 轮询）。
   * 预设/Live Tail 落地为 now 终点的相对滑窗；自定义区间用所选起止。
   */
  const onTimeRangeChange = (range: TimeRange) => {
    setTimeRangeValue(range);
    const preset = TIME_RANGE_PRESETS.find((item) => item.key === range.preset);
    const window: [Dayjs, Dayjs] = preset
      ? [dayjs().subtract(preset.ms), dayjs()]
      : [dayjs(range.from), dayjs(range.to)];
    runWithRange(window);
  };

  // 定时刷新：相对范围（预设/Live Tail 滑窗）按所选间隔定时重查；自定义区间自动停用
  const runRef = useRef(runCurrent);
  useEffect(() => {
    runRef.current = runCurrent;
  });
  useEffect(() => {
    if (isCustomRange || refreshIntervalSec === 0) {
      return;
    }

    const timer = setInterval(() => runRef.current(undefined, true), refreshIntervalSec * 1000);
    return () => clearInterval(timer);
  }, [isCustomRange, refreshIntervalSec]);

  // 首次进入与数据源切换：以当前查询运行（时间范围在下一次运行时生效）；
  // 已保存搜索上下文跳过默认首查，由加载的快照驱动；traceId 深链场景同样跳过，由下方深链 effect 负责首查（避免双查询竞态）
  const traceIdParam = searchParams.get('traceId');
  useEffect(() => {
    if (savedId === undefined && traceIdParam == null) {
      runRef.current();
    }

    void loadSavedList();
  }, [source, savedId, loadSavedList, traceIdParam]);

  // traceId 深链落地（日志点 trace_id 跳转 / 分享链接直达）：切 Lucene 模式 + traces 默认列/排序 + trace_id 过滤器，立即按该链路查询
  useEffect(() => {
    if (traceIdParam == null || traceIdParam.length === 0 || source !== 'traces') {
      return;
    }

    const traceFilter: SearchFilter = { id: Date.now(), field: 'trace_id', op: 'eq', value: traceIdParam };
    setSyntax('lucene');
    setWhereText('');
    setFieldsText(DEFAULT_FIELDS.traces);
    setOrderByText(DEFAULT_ORDER_BY.traces);
    setFilters([traceFilter]);
    setDetailRow(null);
    try {
      const built = buildLuceneQuery({ fields: DEFAULT_FIELDS.traces, orderBy: DEFAULT_ORDER_BY.traces, whereText: '', filters: [traceFilter] });
      void execute(built, currentTimeRange(), limit);
    } catch (err) {
      resetPages();
      setResult(null);
      setError(err instanceof Error ? err.message : String(err));
    }
    // 深链落地一次性动作：仅随 traceId 参数/数据域变化（useSearchParams 派生值不进依赖）
  }, [traceIdParam, source]);

  // 保存的搜索恢复：状态批量落地后以恢复完的查询/时间窗重查一次
  useEffect(() => {
    if (!pendingRunRef.current) {
      return;
    }

    pendingRunRef.current = false;
    runRef.current();
  });

  // URL 条件同步：条件状态单向投影到地址栏（replace 不产生历史记录），复制地址即可分享当前查询条件。
  // traceId 为既有深链参数原样透传（深链落地后条件已物化为 filters，参数保留维持「浏览器回退返回日志视图」语义）；
  // savedId 上下文由快照驱动，不同步。目标参数集与当前地址一致时跳过写入，避免导航回环
  const serializedParams = useMemo(
    () => serializeSearchUrlState({
      source,
      syntax,
      whereText,
      sqlWhere,
      fieldsText,
      orderByText,
      filters,
      denoise,
      timeRange: timeRangeValue,
      limit,
      refreshIntervalSec,
    }),
    [source, syntax, whereText, sqlWhere, fieldsText, orderByText, filters, denoise, timeRangeValue, limit, refreshIntervalSec],
  );
  useEffect(() => {
    if (savedId !== undefined) {
      return;
    }

    const next = new URLSearchParams(serializedParams);
    const traceId = searchParams.get('traceId');
    if (traceId) {
      next.set('traceId', traceId);
    }

    if (next.toString() === searchParams.toString()) {
      return;
    }

    setSearchParams(next, { replace: true });
  }, [serializedParams, searchParams, savedId, setSearchParams]);

  const switchSource = (domain: SearchDomain) => {
    if (domain === source) {
      return;
    }

    // 切域是上下文变更而非刷新：清空旧域结果，回退到首查加载态（避免无指示地滞留跨域旧数据）；
    // 条件适配（默认字段/排序跟随、过滤器清空、详情抽屉关闭）统一由上方渲染期派生修正处理（含回退还原路径）
    resetPages();
    setResult(null);
    setHistogram(null);
    setSearchParams({ source: domain }, { replace: true });
  };

  /** trace_id 跳转链路视图：深链 URL（push 历史栈，浏览器回退可返回日志视图），由深链 effect 落地为过滤查询. */
  const openTrace = (traceId: string) => {
    setDetailRow(null);
    setSearchParams({ source: 'traces', traceId });
  };

  const openDetail = (row: Record<string, unknown>) => {
    const range = searchedRange ?? currentTimeRange();
    setDetailRange([range[0].toISOString(), range[1].toISOString()]);
    setDetailRow(row);
  };

  /** trace_id 激活分流：日志域深链跳转，链路域打开统一侧栏. */
  const activateTrace = (traceId: string) => {
    if (source === 'logs') {
      openTrace(traceId);
    } else {
      openDetail({ trace_id: traceId });
    }
  };

  /** 显示列变更：回写状态并即时重查（行1 关键字条两种语法共用）. */
  const onFieldsChange = (value: string) => {
    setFieldsText(value);
    runCurrent({ fields: value });
  };

  /** 排序变更：回写状态并即时重查（行1 关键字条两种语法共用）. */
  const onOrderByChange = (value: string) => {
    setOrderByText(value);
    runCurrent({ orderBy: value });
  };

  /** 结果行排序（列头菜单）：回写排序关键字条并即时重查. */
  const onSortColumns = (field: string, desc: boolean) => {
    const orderBy = `${field} ${desc ? 'DESC' : 'ASC'}`;
    setOrderByText(orderBy);
    runCurrent({ orderBy });
  };

  /** 添加等值过滤 chip（列头/明细联动入口）. */
  const addEqualFilter = (field: string, value: unknown, domain: SearchDomain = source) => {
    if (domain !== source) {
      pendingDomainFilter.current = [{ id: Date.now(), field, op: 'eq', value: String(value) }];
      setWhereText(''); setSqlWhere(''); setSyntax('lucene');
      switchSource(domain);
      return;
    }
    setFilters((prev) => [...prev, { id: Date.now(), field, op: 'eq', value: String(value) }]);
  };

  /** 「仅此值」（详情抽屉联动）：清空该字段其它值过滤，仅保留此值（HyperDX Only 式）. */
  const filterOnlyValue = (field: string, value: unknown, domain: SearchDomain = source) => {
    if (domain !== source) { addEqualFilter(field, value, domain); return; }
    setFilters((prev) => applyValueOnly(prev, field, String(value)));
  };

  /** 结果行对象化（列名 → 单元格值），供表格 / JSON 视图使用. */
  const rows = useMemo<Record<string, unknown>[]>(() => {
    if (!result) {
      return [];
    }

    return result.rows.map((cells) => Object.fromEntries(result.columns.map((name, i) => [name, cells[i]])));
  }, [result]);

  /** 直方图桶轴（点击缩放按 dataIndex 反查桶起点 + 当前步长）. */
  const histogramAxisRef = useRef<{ starts: number[]; stepMs: number }>({ starts: [], stepMs: 60_000 });

  /** 直方图配置：ClickStack 式高密度细柱（无 Y 轴/网格）；服务端全量聚合分桶，聚合失败回退返回行分桶. */
  const histogramOption = useMemo<EChartsOption>(() => {
    const [from, to] = searchedRange ?? currentTimeRange();
    const stepMs = pickBucketStepMs(Math.max(60_000, to.diff(from)));
    const spansDays = to.diff(from, 'day', true) >= 1;
    const labelFormat = spansDays ? 'MM-DD HH:mm' : 'HH:mm:ss';
    const hasData = !!result && result.rows.length > 0 && result.columns.includes('ts');

    const base = {
      animation: false,
      tooltip: { trigger: 'axis' as const },
      // HyperDX 式细柱直方图：仅保留少量 y 轴刻度（25/100…）与稀疏时间标签，无网格
      grid: { left: 34, right: 12, top: 6, bottom: 20 },
      xAxis: { type: 'category' as const, axisLabel: { fontSize: 10, color: cssTheme.textFaint }, axisTick: { show: false }, axisLine: { lineStyle: { color: cssTheme.border } } },
      yAxis: { type: 'value' as const, axisLabel: { show: true, fontSize: 9, color: cssTheme.textFaint, formatter: (value: number) => (value >= 1000 ? `${Math.round(value / 1000)}k` : String(value)) }, splitLine: { show: false }, axisLine: { show: false }, splitNumber: 2 },
      series: [] as EChartsOption['series'],
    };

    if (!histogram && !hasData) {
      // 空轴：按所选时间范围铺满刻度，保持常驻图表框架（不可点击缩放）
      histogramAxisRef.current = { starts: [], stepMs };
      const start = from.valueOf();
      const span = Math.max(1, to.valueOf() - start);
      const steps = 12;
      return {
        ...base,
        xAxis: { ...base.xAxis, data: Array.from({ length: steps + 1 }, (_, i) => dayjs(start + (span * i) / steps).format(labelFormat)) },
      };
    }

    const groupOrder = source === 'logs' ? SEVERITY_ORDER : STATUS_ORDER;
    let bucketStarts: number[];
    let counts: Map<string, number[]>;
    let groups: string[];

    if (histogram) {
      // 服务端全量聚合结果（全桶零填充轴，计数为范围内匹配总数）
      bucketStarts = histogram.bucketStarts;
      counts = histogram.counts;
      groups = histogram.groups;
    } else {
      // 回退（聚合查询失败）：对返回行客户端分桶，同样零填充全桶轴保持时间位置正确
      const tsIndex = result!.columns.indexOf('ts');
      const groupField = HISTOGRAM_GROUP_FIELD[source];
      const groupIndex = result!.columns.includes(groupField) ? result!.columns.indexOf(groupField) : -1;
      const start = Math.floor(from.valueOf() / stepMs) * stepMs;
      const length = Math.max(1, Math.ceil((to.valueOf() - start) / stepMs));
      bucketStarts = Array.from({ length }, (_, index) => start + index * stepMs);
      const indexByBucket = new Map(bucketStarts.map((bucket, index) => [bucket, index]));
      counts = new Map();
      const seen = new Set<string>();
      for (const cells of result!.rows) {
        const bucket = Math.floor(dayjs(String(cells[tsIndex])).valueOf() / stepMs) * stepMs;
        const index = indexByBucket.get(bucket);
        if (index === undefined) {
          continue;
        }

        const group = groupIndex >= 0 ? String(cells[groupIndex] ?? '-') : 'count';
        seen.add(group);
        const series = counts.get(group) ?? new Array<number>(length).fill(0);
        series[index] += 1;
        counts.set(group, series);
      }

      groups = [...seen].sort((a, b) => groupOrder.indexOf(a) - groupOrder.indexOf(b));
    }

    histogramAxisRef.current = { starts: bucketStarts, stepMs };

    return {
      ...base,
      xAxis: {
        ...base.xAxis,
        data: bucketStarts.map((bucket) => dayjs(bucket).format(labelFormat)),
      },
      series: groups.map((group) => ({
        name: group,
        type: 'bar' as const,
        stack: 'total',
        barMaxWidth: 14,
        itemStyle: { color: GROUP_COLORS[group] ?? cssTheme.primary },
        data: counts.get(group) ?? new Array<number>(bucketStarts.length).fill(0),
      })),
    };
  }, [histogram, result, searchedRange, currentTimeRange, source, cssTheme]);

  /** 直方图点击 → 缩放到该时间桶（自定义区间重查）. */
  const chartEvents = useMemo<ChartEventMap>(() => ({
    click: (params) => {
      const { starts, stepMs } = histogramAxisRef.current;
      const bucket = starts[params.dataIndex ?? -1];
      if (bucket === undefined) {
        return;
      }

      setTimeRangeValue({ from: dayjs(bucket).toISOString(), to: dayjs(bucket + stepMs).toISOString() });
      runWithRange([dayjs(bucket), dayjs(bucket + stepMs)]);
    },
  }), [syntax, sqlWhere, whereText, fieldsText, orderByText, filters, denoise, limit, source]);

  /** 表结构面板：点击字段追加条件片段（Lucene 追加 `field:`，SQL 追加字段名到 WHERE 片段）. */
  const onSchemaFieldClick = (field: string) => {
    if (syntax === 'lucene') {
      setWhereText((prev) => (prev.length === 0 ? `${field}:` : `${prev} ${field}:`));
      return;
    }

    setSqlWhere((prev) => (prev.trim().length === 0 ? field : `${prev.trimEnd()} ${field}`));
  };

  const schemaContent = (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          type="button"
          aria-label={t('search.schema')}
          title={t('search.schema')}
          className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
        >
          <Table2 className="size-3.5" aria-hidden />
        </button>
      </Popover.Trigger>
      <Popover.Content className="max-w-[360px]">
        <div className="text-xs leading-relaxed text-muted">{t('search.schemaHint')}</div>
        <div className="mt-2 flex flex-wrap gap-1">
          {SEARCH_FIELDS[source].map((meta) => (
            <button
              key={meta.field}
              type="button"
              onClick={() => onSchemaFieldClick(meta.field)}
              className="cursor-pointer rounded-sm border border-border bg-surface-2 px-1.5 py-0.5 font-mono text-[11px] text-fg transition-colors hover:border-primary/50 hover:text-primary"
            >
              {meta.field}
            </button>
          ))}
        </div>
      </Popover.Content>
    </Popover.Root>
  );

  const applySavedSearch = (item: SavedQueryItem) => {
    try {
      const definition = JSON.parse(item.definition) as SavedSearchDefinition;
      resetPages();
      setFilters(definition.filters ?? []);
      setDenoise(definition.denoise ?? false);
      const preset = TIME_RANGE_PRESETS.find((candidate) => candidate.ms === definition.rangeMs);
      if (preset) {
        setTimeRangeValue({
          from: dayjs().subtract(preset.ms).toISOString(),
          to: dayjs().toISOString(),
          preset: preset.key,
        });
      } else if (definition.rangeMs) {
        setTimeRangeValue({
          from: dayjs().subtract(definition.rangeMs).toISOString(),
          to: dayjs().toISOString(),
        });
      }

      const domain = definition.domain;
      if (definition.mode === 'sql') {
        // SQL 模式快照：行2 为 WHERE 片段，行1 字段/排序与 Lucene 同构；
        // 历史整句 SQL（sql/query 字段）回退提取 WHERE 主体（时间窗样板剥离）
        setSyntax('sql');
        setSqlWhere(definition.where ?? extractWhereFragment(definition.sql ?? definition.query ?? ''));
        setFieldsText(definition.fields ?? DEFAULT_FIELDS[domain ?? source]);
        setOrderByText(definition.orderBy ?? DEFAULT_ORDER_BY[domain ?? source]);
        if (definition.limit) {
          setLimit(definition.limit);
        }
      } else {
        // Lucene 模式快照：恢复完整查询状态
        setSyntax('lucene');
        setWhereText(definition.where ?? '');
        setFieldsText(definition.fields ?? DEFAULT_FIELDS[domain ?? source]);
        setOrderByText(definition.orderBy ?? DEFAULT_ORDER_BY[domain ?? source]);
        if (definition.limit) {
          setLimit(definition.limit);
        }
      }

      if (domain && domain !== source) {
        pendingDomainFilter.current = definition.filters ?? [];
        setSearchParams({ source: domain }, { replace: true });
        return;
      }

      // 状态批量恢复后由 pending-run effect 重查（同步 runRef 会读到恢复前的旧状态）
      pendingRunRef.current = true;
    } catch {
      // definition JSON 损坏时忽略
    }
  };

  /** 当前搜索状态 → 保存快照（搜索页 / 已保存搜索上下文共用；SQL 模式行1/行2 与 Lucene 同构同形）. */
  const buildDefinition = (): SavedSearchDefinition => syntax === 'lucene'
    ? { domain: source, mode: 'lucene', where: whereText, fields: fieldsText, orderBy: orderByText, limit, rangeMs, filters, denoise }
    : { domain: source, mode: 'sql', where: sqlWhere.trim(), fields: fieldsText, orderBy: orderByText, limit, rangeMs, filters, denoise };

  /** 新建保存（搜索页入口：保存搜索弹窗命名后创建新条目）. */
  const submitSave = async () => {
    const name = saveName.trim();
    if (name.length === 0) {
      return;
    }

    setSaving(true);
    try {
      await saveQuery({ name, kind: 'search', definition: JSON.stringify(buildDefinition()) });
      setSaveOpen(false);
      setSaveName('');
      await loadSavedList();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };

  /** 已保存搜索上下文入口：以当前条件覆盖更新打开的条目（保存即更新，不新建）. */
  const submitUpdate = async () => {
    if (!activeSaved) {
      return;
    }

    setSaving(true);
    try {
      await saveQuery({ id: activeSaved.exactId ?? activeSaved.id, name: activeSaved.name, kind: 'search', definition: JSON.stringify(buildDefinition()) });
      toast.success(t('search.savedSearchUpdated'));
      await loadSavedList();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };

  const removeSaved = async (id: number | string) => {
    await deleteSavedQuery(id);
    await loadSavedList();
    if (activeSaved && String(activeSaved.exactId ?? activeSaved.id) === String(id)) {
      // 删除的是当前打开的已保存搜索：回到列表页
      navigate('/saved-searches');
    }
  };

  // 已保存搜索上下文：加载快照并应用（打开即呈现对应画面；保存即更新当前条目）
  useEffect(() => {
    if (savedId === undefined || !/^[1-9]\d*$/.test(String(savedId))) {
      return;
    }

    let cancelled = false;
    getSavedQuery(savedId)
      .then((item) => {
        if (cancelled) {
          return;
        }

        if (item.kind !== 'search') {
          toast.error(t('savedSearches.notSearch'));
          navigate('/saved-searches');
          return;
        }

        setActiveSaved(item);
        applySavedSearch(item);
      })
      .catch((err) => {
        if (cancelled) {
          return;
        }

        toast.error(err instanceof Error ? err.message : String(err));
        navigate('/saved-searches');
      });
    return () => {
      cancelled = true;
    };
    // 仅在进入/切换已保存搜索时加载一次；applySavedSearch 内部只做状态恢复
  }, [savedId]);

  /** HyperDX 式空态：整幅浅灰块（无边框无圆角）+ 未找到匹配结果 + 语法提示 + 实际搜索时间范围. */
  const emptyState = (
    <div className="bg-surface-2/40 px-6 py-8 text-center" data-testid="search-empty">
      <div className="text-sm font-semibold text-fg">{t('search.noResultsTitle')}</div>
      <div className="mt-3 text-xs text-muted">{t('search.emptySyntaxHint')}</div>
      <div className="mt-3 text-xs text-muted">
        {`${t('search.searchedRange')}：${isCustomRange ? t('search.custom') : timeRangeValue.preset}（${searchedRange ? `${formatDateTime(searchedRange[0].toISOString())} ${t('search.rangeTo')} ${formatDateTime(searchedRange[1].toISOString())}` : '-'}）`}
      </div>
    </div>
  );

  const exportCsv = () => {
    if (!result || result.rows.length === 0) {
      return;
    }

    downloadCsv(visibleColumns, rows.map((row) => visibleColumns.map((column) => row[column])), `sdata-${source}-search.csv`);
  };

  /** 复制生成的 SQL（生成 SQL 弹层内分节复制）. */
  const copySql = (text: string) => {
    void navigator.clipboard?.writeText(text).then(() => toast.success(t('common.copied')));
  };

  /** 结果头部计数（聚合直方图存在时为范围内精确总数；否则为返回行数，截断以 + 标注）. */
  const countLabel = t('search.resultsCount', {
    count: histogram ? histogram.total.toLocaleString() : `${result?.rows.length ?? 0}${result?.truncated ? '+' : ''}`,
  });

  return (
    <Page className="font-mono text-[13px]">
      <div className="flex h-full flex-col">
        {/* 行1（HyperDX 式）：数据源 + SELECT/ORDER BY 关键字条 + 保存操作 */}
        <div className="flex items-center gap-2 pb-2">
          <div className="w-[112px] shrink-0">
            <Select
              value={source}
              onValueChange={(value) => switchSource(value as SearchDomain)}
              testId="search-source"
              aria-label="source"
              options={[
                { value: 'logs', label: <span className="flex items-center gap-1.5"><FileSearch className="size-3.5" aria-hidden />{t('search.sourceLogs')}</span> },
                { value: 'traces', label: <span className="flex items-center gap-1.5"><Workflow className="size-3.5" aria-hidden />{t('search.sourceTraces')}</span> },
              ]}
            />
          </div>
          {/* 行1 关键字条两种语法同构（HyperDX 式）：SELECT 定义显示列、ORDER BY 定义排序，SQL 模式不换位 */}
          <div className="min-w-0 flex-[3_1_0%]">
            <SelectColumnsPopover
              domain={source}
              value={fieldsText}
              onChange={onFieldsChange}
              from={facetRange ? facetRange[0].toISOString() : null}
              to={facetRange ? facetRange[1].toISOString() : null}
              denoise={denoise}
            />
          </div>
          <div className="min-w-0 flex-1">
            <SortEditorPopover domain={source} value={orderByText} onChange={onOrderByChange} />
          </div>
          {/* 单一入口（HyperDX 式）：Save search 弹层 = 保存当前搜索 + 已保存列表 */}
          <div className="ml-auto flex shrink-0 items-center gap-2">
            {activeSaved && (
              <Tooltip content={activeSaved.name}>
                <Tag tone="primary" className="max-w-[180px]">
                  <span className="truncate">{activeSaved.name}</span>
                </Tag>
              </Tooltip>
            )}
            <Button icon={<Bell className="size-3.5" aria-hidden />} onClick={() => setAlertsOpen(true)}>{t('alerts.title')}</Button>
            <Popover.Root>
              <Popover.Trigger asChild>
                <Button icon={<Save className="size-3.5" aria-hidden />} testId="search-saved-button">
                  {t('search.save')}
                </Button>
              </Popover.Trigger>
              <Popover.Content align="end" className="w-[280px]">
                {canWrite && (
                  <>
                    {activeSaved ? (
                      <Popover.Close asChild>
                        <Button
                          variant="ghost"
                          size="sm"
                          className="w-full justify-start"
                          icon={<Save className="size-3.5" aria-hidden />}
                          loading={saving}
                          onClick={() => void submitUpdate()}
                        >
                          {t('search.updateCurrentSearch')}
                        </Button>
                      </Popover.Close>
                    ) : (
                      <Popover.Close asChild>
                        <Button
                          variant="ghost"
                          size="sm"
                          className="w-full justify-start"
                          icon={<Save className="size-3.5" aria-hidden />}
                          onClick={() => setSaveOpen(true)}
                        >
                          {t('search.saveCurrentSearch')}
                        </Button>
                      </Popover.Close>
                    )}
                    <div className="my-1.5 border-t border-border/60" />
                  </>
                )}
                <div className="px-1 pb-1 pt-0.5 text-[11px] text-subtle">{t('search.savedSearches')}</div>
                {savedList.length === 0 ? (
                  <div className="px-1 py-2 text-xs text-faint">{t('search.noSavedSearches')}</div>
                ) : (
                  <div className="flex max-h-[280px] flex-col gap-0.5 overflow-auto">
                    {savedList.map((item) => (
                      <div key={item.id} className="flex items-center gap-1">
                        <Button
                          variant="ghost"
                          size="sm"
                          className="min-w-0 flex-1 justify-start"
                          onClick={() => (activeSaved ? navigate(`/saved-searches/${item.exactId ?? item.id}`) : applySavedSearch(item))}
                        >
                          <span className="truncate">{item.name}</span>
                        </Button>
                        {canWrite && (
                          <Popconfirm
                            title={t('common.confirmDelete')}
                            okText={t('common.delete')}
                            cancelText={t('common.cancel')}
                            onConfirm={() => void removeSaved(item.exactId ?? item.id)}
                          >
                            <Button variant="ghost" size="sm" danger aria-label={t('common.delete')}>×</Button>
                          </Popconfirm>
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </Popover.Content>
            </Popover.Root>
          </div>
        </div>

        {/* 行2（ClickStack 式单行工具条，两种语法同构）：语法切换 + 表结构 + Lucene 搜索栏 / SQL 提示
            + 同行右侧：时间范围/Live Tail + 定时刷新 + 行数 + 运行 */}
        <div className="flex items-center gap-2 pb-2">
          <div className="w-[112px] shrink-0">
            <Select
              value={syntax}
              onValueChange={(value) => setSyntax(value as 'lucene' | 'sql')}
              testId="search-syntax"
              aria-label="syntax"
              options={[
                { value: 'lucene', label: 'Lucene' },
                { value: 'sql', label: 'SQL' },
              ]}
            />
          </div>
          {schemaContent}
          {/* 行2 WHERE 定义栏（同位不同语法）：Lucene/SQL 均带智能建议（字段/JSON 路径/值/关键字，Enter 无弹层时运行） */}
          <div className="min-w-0 flex-1">
            {syntax === 'lucene' ? (
              <WhereSuggestInput
                domain={source}
                syntax="lucene"
                value={luceneExpression}
                onChange={onLuceneChange}
                onRun={() => runRef.current()}
                className="w-full font-mono text-[13px]"
                placeholder={t('search.wherePlaceholder')}
                testId="search-where"
                from={facetRange ? facetRange[0].toISOString() : null}
                to={facetRange ? facetRange[1].toISOString() : null}
                denoise={denoise}
              />
            ) : (
              <WhereSuggestInput
                domain={source}
                syntax="sql"
                value={sqlWhere}
                onChange={setSqlWhere}
                onRun={() => runRef.current()}
                className="w-full font-mono text-[13px]"
                placeholder={t('search.sqlWherePlaceholder')}
                testId="search-where-sql"
                from={facetRange ? facetRange[0].toISOString() : null}
                to={facetRange ? facetRange[1].toISOString() : null}
                denoise={denoise}
              />
            )}
          </div>
          {/* 时间筛选 + 定时刷新 + 行数 + 运行：与查询输入同一行（HyperDX/ClickStack 式） */}
          <div className="ml-auto flex shrink-0 items-center gap-2">
            <TimeRangePicker
              value={timeRangeValue}
              onChange={onTimeRangeChange}
              presets={TIME_RANGE_PRESETS}
              align="center"
              testId="search-live-tail"
            />
            {/* 定时刷新（HyperDX 式紧随其后的 10s 选择器；自定义区间停用） */}
            <Tooltip content={isCustomRange ? t('search.refreshDisabledCustom') : null}>
              <div className="w-[84px] shrink-0">
                <Select
                  value={String(refreshIntervalSec)}
                  onValueChange={(value) => setRefreshIntervalSec(Number(value))}
                  disabled={isCustomRange}
                  testId="search-refresh-interval"
                  aria-label="auto refresh"
                  options={[
                    { value: '0', label: t('search.refreshOff') },
                    ...REFRESH_INTERVAL_OPTIONS.map((sec) => ({ value: String(sec), label: sec < 60 ? `${sec}s` : `${sec / 60}m` })),
                  ]}
                />
              </div>
            </Tooltip>
            <div className="w-[112px] shrink-0">
              <Select
                value={String(limit)}
                onValueChange={(value) => setLimit(Number(value))}
                testId="search-limit"
                aria-label="limit"
                options={LIMIT_OPTIONS.map((value) => ({ value: String(value), label: `${t('search.limit')} ${value}` }))}
              />
            </div>
            {/* 无感刷新：已有结果时后台静默拉取，不闪加载图标（仅首查/出错后显示） */}
            <Button
              variant="secondary"
              icon={<Play className="size-3.5" aria-hidden />}
              loading={loading && !result}
              onClick={() => runRef.current()}
              testId="search-run"
            >
              {t('search.run')}
            </Button>
          </div>
        </div>

        {syntax === 'lucene' && <LuceneSearchHelp source={source} value={luceneExpression} />}

        {error && (
          <div className="mb-2 flex items-center justify-between gap-2 bg-error-soft px-3 py-2 text-sm font-sans text-error">
            <span className="min-w-0 break-words">{error}</span>
            <button type="button" aria-label="dismiss" onClick={() => setError(null)} className="cursor-pointer font-bold">
              ×
            </button>
          </div>
        )}

        {/* 主区：左侧分析面板 + 右侧（结果头部 + 直方图 + 结果表/JSON） */}
        <div className="flex min-h-0 flex-1 items-stretch gap-2">
          <SearchSidePanel
            domain={source}
            mode={analysisMode}
            onModeChange={setAnalysisMode}
            denoise={denoise}
            onDenoiseChange={setDenoise}
            filters={filters}
            onFiltersChange={setFilters}
            from={facetRange ? facetRange[0].toISOString() : null}
            to={facetRange ? facetRange[1].toISOString() : null}
          />
          <div className="flex min-w-0 flex-1 flex-col gap-2">
            {/* 结果头部：N 条结果（左）+ 耗时 + 视图切换/CSV 导出（右），HyperDX 式等宽 */}
            <div className="flex items-center justify-between gap-2">
              <code className="font-mono text-xs text-muted" data-testid="search-result-count">
                {countLabel}
              </code>
              <div className="flex items-center gap-1">
                {result && (
                  <code className="mr-1 font-mono text-xs text-muted">
                    {`${t('search.elapsed')} ${result.elapsedMs}ms`}
                  </code>
                )}
                <Tooltip content={viewMode === 'table' ? t('search.rawJsonView') : t('search.tableView')}>
                  <button
                    type="button"
                    aria-label={viewMode === 'table' ? t('search.rawJsonView') : t('search.tableView')}
                    onClick={() => setViewMode((mode) => (mode === 'table' ? 'json' : 'table'))}
                    className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
                  >
                    {viewMode === 'table' ? <Braces className="size-3.5" aria-hidden /> : <Table2 className="size-3.5" aria-hidden />}
                  </button>
                </Tooltip>
                <Tooltip content={t('search.generatedSql')}>
                  <button
                    type="button"
                    aria-label={t('search.generatedSql')}
                    disabled={!lastSql}
                    onClick={() => setGeneratedSqlOpen(true)}
                    className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg disabled:pointer-events-none disabled:opacity-40"
                  >
                    <CodeXml className="size-3.5" aria-hidden />
                  </button>
                </Tooltip>
                <Tooltip content={t('search.exportCsv')}>
                  <button
                    type="button"
                    aria-label={t('search.exportCsv')}
                    disabled={!result || result.rows.length === 0}
                    onClick={exportCsv}
                    className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg disabled:pointer-events-none disabled:opacity-40"
                  >
                    <Download className="size-3.5" aria-hidden />
                  </button>
                </Tooltip>
              </div>
            </div>

            {analysisMode === 'patterns' ? (
              <div className="flex flex-1 items-center justify-center">
                <Empty description={t('search.patternsDevPlaceholder')} />
              </div>
            ) : viewMode === 'json' ? (
              /* 原始 JSON 视图（前 500 行）：平铺区域，顶部细线与结果头部分隔 */
              <pre
                className="min-h-0 flex-1 overflow-auto border-t border-border pt-2 font-mono text-xs break-all whitespace-pre-wrap text-fg"
                data-testid="search-json-view"
              >
                {rows.length > 0 ? JSON.stringify(rows.slice(0, 500), null, 2) : t('search.noResultsTitle')}
              </pre>
            ) : (
              <>
                {/* 直方图：细柱堆叠（点击柱形缩放到该时间桶），平铺无框，x 轴线即分隔线 */}
                <div className="h-[110px] shrink-0 px-2 pt-1" title={t('search.histogramZoomHint')}>
                  <Chart option={histogramOption} style={{ height: 106 }} onEvents={chartEvents} />
                </div>

                {/* 结果表：无分页 + 行点击打开详情抽屉（行首箭头就地展开明细） */}
                <div className="min-h-0 flex-1">
                  <ResultsTable
                    domain={source}
                    columns={visibleColumns}
                    rows={rows}
                    /* 无感刷新：仅在无结果可展示时出加载遮罩，轮询更新不闪烁 */
                    loading={loading && !result}
                    queryGeneration={queryGeneration}
                    hasMore={!!result?.truncated && !loading && !error}
                    loadingMore={loadingMore}
                    loadMoreError={loadMoreError}
                    onLoadMore={loadMore}
                    complete={!!result && !result.truncated && !loading && !error}
                    empty={emptyState}
                    onSort={onSortColumns}
                    onAddFilter={(field) => addEqualFilter(field, '')}
                    onTraceOpen={activateTrace}
                    onRowOpen={openDetail}
                    onFilterByValue={addEqualFilter}
                  />
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      {alertsOpen && <SearchAlertsModal open={alertsOpen} onOpenChange={setAlertsOpen} definition={buildDefinition()} savedQueryId={activeSaved ? activeSaved.exactId ?? String(activeSaved.id) : undefined} canWrite={canWrite} />}

      {/* 保存搜索弹窗（Save search 弹层的「保存当前搜索」入口） */}
      <Modal
        open={saveOpen}
        onOpenChange={setSaveOpen}
        title={t('search.save')}
        footer={(
          <>
            <Button onClick={() => setSaveOpen(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" loading={saving} onClick={() => void submitSave()}>
              {t('common.save')}
            </Button>
          </>
        )}
      >
        <Input
          value={saveName}
          onChange={(event) => setSaveName(event.target.value)}
          placeholder={t('search.saveNamePlaceholder')}
          maxLength={64}
        />
        <div className="mt-2 font-mono text-xs break-all text-subtle">
          {`${source} · ${syntax === 'lucene' ? whereText || '-' : sqlWhere.trim() || '-'}`}
        </div>
      </Modal>

      {/* 生成 SQL 弹层（HyperDX 式）：最近一次实际执行的 SQL，只读高亮 + 分节复制 */}
      {lastSql != null && (
        <Modal open={generatedSqlOpen} onOpenChange={setGeneratedSqlOpen} title={t('search.generatedSql')} width={760}>
          <div className="flex flex-col gap-4">
            <section>
              <div className="mb-1.5 flex items-center justify-between">
                <span className="text-xs font-medium text-muted">{t('search.querySql')}</span>
                <Button size="sm" variant="link" icon={<Copy className="size-3" aria-hidden />} onClick={() => copySql(lastSql)}>
                  {t('search.copySql')}
                </Button>
              </div>
              <div className="overflow-hidden rounded-md border border-border">
                <SqlEditor value={lastSql} readOnly height="280px" />
              </div>
            </section>
            {lastHistogramSql != null && (
              <section>
                <div className="mb-1.5 flex items-center justify-between">
                  <span className="text-xs font-medium text-muted">{t('search.histogramSql')}</span>
                  <Button size="sm" variant="link" icon={<Copy className="size-3" aria-hidden />} onClick={() => copySql(lastHistogramSql)}>
                    {t('search.copySql')}
                  </Button>
                </div>
                <div className="overflow-hidden rounded-md border border-border">
                  <SqlEditor value={lastHistogramSql} readOnly height="200px" />
                </div>
              </section>
            )}
          </div>
        </Modal>
      )}

      {/* 链路瀑布图 */}
      <TraceDetailDrawer row={source === 'traces' ? detailRow : null}
        from={detailRange?.[0] ?? ''}
        to={detailRange?.[1] ?? ''}
        onClose={() => setDetailRow(null)} onFilterByValue={addEqualFilter} onFilterOnlyValue={filterOnlyValue} />

      {/* 日志/span 详情抽屉（HyperDX 式侧栏：概览/列值/链路日志/服务地图/上下文） */}
      <LogDetailDrawer
        domain={source}
        row={source === 'logs' ? detailRow : null}
        onClose={() => setDetailRow(null)}
        onFilterByValue={addEqualFilter}
        onFilterOnlyValue={filterOnlyValue}
        onTraceActivate={activateTrace}
      />
    </Page>
  );
}
