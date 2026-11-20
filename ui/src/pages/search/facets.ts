import { useEffect, useRef, useState } from 'react';
import { searchSql } from '@/api/search';
import type { AnalyticsQueryResponse, SearchDomain, SearchSqlParameters } from '@/api/types';
import type { ChartSource } from '@/api/chartTypes';
import { DENOISE_SQL, fieldExpression } from './lucene';
import { bindJsonPath, duckJsonPath, type JsonSegment } from './jsonFields';

/** facet 组初始条数与 Load more 步长（GROUP BY 聚合行数上限，非分页；服务端硬上限 10000）. */
const FACET_INITIAL_LIMIT = 10;
const FACET_MORE_STEP = 50;

/**
 * facet 聚合行抽样（ClickStack 式 "estimated from a sample of rows"）：
 * 固定行数 reservoir 采样（本仓库 DuckDB 不接受 ROWS 后跟种子参数，语法经 Store 后端测试实证），
 * 计数为样本估计值而非全量精确统计，高基数属性免于全表 unnest/GROUP BY 扫描；
 * 跨查询各自重采样，估计值允许轻微波动.
 */
const FACET_SAMPLE_ROWS = 1000;
const SAMPLE_SQL = `USING SAMPLE ${FACET_SAMPLE_ROWS} ROWS`;

/** LIKE 字面转义（\ % _），配合 ILIKE ... ESCAPE，用户输入经 $key 参数绑定不参与 SQL 组装. */
export function escapeLike(term: string): string {
  return term.replace(/[\\%_]/g, '\\$&');
}

/** 搜索词 → ILIKE 参数值（%term%）；空词返回 null（不附加搜索条件）. */
export function likePattern(term: string): string | null {
  const trimmed = term.trim();
  return trimmed.length > 0 ? `%${escapeLike(trimmed)}%` : null;
}

/**
 * 过滤器树分组配置（对应 HyperDX Properties 侧栏）.
 * jsonColumn 组展开为 key 列表（key 再展开值列表）；column 组直接展开为值列表.
 * ScopeVersion 不做：OTLP scope 在 ingest 时被丢弃，后端无此数据.
 */
export interface FacetGroupConfig {
  /** 组 id（testid 与查询复位 key） */
  id: string;
  /** i18n key */
  labelKey: string;
  /** 物理列（白名单列名，来自 SEARCH_FIELDS） */
  column?: string;
  /** JSON 属性列（attributes/resource，logs 另有 body——JSON 根白名单见 jsonFields.jsonRoots） */
  jsonColumn?: string;
  /** 默认展开 */
  defaultOpen?: boolean;
}

/** 各数据域过滤器树分组（顺序对应 HyperDX：属性组在前，物理列组在后）. */
export const FACET_GROUPS: Record<SearchDomain, FacetGroupConfig[]> = {
  logs: [
    { id: 'resource', labelKey: 'search.facetGroupResource', jsonColumn: 'resource' },
    { id: 'attributes', labelKey: 'search.facetGroupLogAttributes', jsonColumn: 'attributes', defaultOpen: true },
    { id: 'body', labelKey: 'search.facetGroupBody', jsonColumn: 'body' },
    { id: 'service', labelKey: 'search.facetGroupService', column: 'service' },
    { id: 'severity', labelKey: 'search.facetGroupSeverity', column: 'severity' },
    { id: 'source', labelKey: 'search.facetGroupSource', column: 'source' },
  ],
  traces: [
    { id: 'resource', labelKey: 'search.facetGroupResource', jsonColumn: 'resource' },
    { id: 'attributes', labelKey: 'search.facetGroupSpanAttributes', jsonColumn: 'attributes' },
    { id: 'name', labelKey: 'search.facetGroupName', column: 'name', defaultOpen: true },
    { id: 'service', labelKey: 'search.facetGroupService', column: 'service', defaultOpen: true },
    { id: 'kind', labelKey: 'search.facetGroupKind', column: 'kind' },
    { id: 'status', labelKey: 'search.facetGroupStatus', column: 'status' },
  ],
};

/** facet 基础 WHERE：时间范围（$start/$end 服务端绑定）+ denoise（仅 logs，与主查询一致）. */
export function facetBaseWhere(domain: ChartSource, denoise: boolean): string {
  return domain === 'logs' && denoise ? `ts >= $start AND ts < $end AND ${DENOISE_SQL}` : 'ts >= $start AND ts < $end';
}

/**
 * JSON 属性组 key 枚举 + 计数（json_valid 防脏数据，unnest 展开每行 key 后聚合；
 * 默认列表行抽样 1000 条（计数为样本估计）；搜索态不加抽样——ILIKE 扫描整个搜索时间窗，
 * 否则稀有值被样本截断搜不到（时间窗口径与 HyperDX 一致：面板查询锚定当前搜索范围）.
 */
export function buildGroupKeysSql(domain: ChartSource, denoise: boolean, jsonColumn: string, limit: number, search = ''): string {
  const guarded = `CASE WHEN json_valid(${jsonColumn}) THEN ${jsonColumn} ELSE '{}' END`;
  return buildFacetSql(
    `SELECT unnest(json_keys(${guarded})) AS v FROM {{${domain}}} WHERE ${facetBaseWhere(domain, denoise)}${sampleClause(search)}`,
    limit,
    search,
  );
}

/**
 * 字段值 top-N + 计数：物理列（白名单直取）或 JSON key（引号路径，key 来自 json_keys 输出，仅转义即安全）；
 * 默认列表行抽样 1000 条，搜索态不加抽样扫描整个时间窗.
 * @throws LuceneCompileError 字段不在白名单或路径不合法
 */
export function buildFieldValuesSql(domain: ChartSource, denoise: boolean, field: string, limit: number, search = '', parameters?: SearchSqlParameters): string {
  const expression = fieldExpression(field, domain, parameters);
  return buildFacetSql(
    `SELECT ${expression} AS v FROM {{${domain}}} WHERE ${facetBaseWhere(domain, denoise)}${sampleClause(search)}`,
    limit,
    search,
  );
}

/**
 * JSON 节点子属性 key 枚举 + 计数（发现树的逐层懒加载）：
 * json_extract 按绑定路径取出节点，json_valid 双重防护（根列 + 节点提取），
 * json_keys unnest 后聚合；抽样/搜索口径与组 key 枚举一致.
 */
export function buildChildKeysSql(domain: ChartSource, denoise: boolean, root: string, segments: JsonSegment[], limit: number, search = '', parameters?: SearchSqlParameters): string {
  const path = bindJsonPath(parameters ?? {}, duckJsonPath(segments));
  const guarded = `CASE WHEN json_valid(${root}) THEN json_extract(${root}, ${path}) ELSE NULL END`;
  const child = `CASE WHEN json_valid(${guarded}) THEN ${guarded} ELSE '{}' END`;
  return buildFacetSql(
    `SELECT unnest(json_keys(${child})) AS v FROM {{${domain}}} WHERE ${facetBaseWhere(domain, denoise)}${sampleClause(search)}`,
    limit,
    search,
  );
}

/** 抽样子句：默认列表加行抽样（成本上界 + 估计计数），搜索态返回空（全窗扫描确保能搜到样本外的值）. */
function sampleClause(search: string): string {
  return likePattern(search) ? '' : ` ${SAMPLE_SQL}`;
}

/** 组装外层过滤/聚合/排序（搜索态附加 v ILIKE $key，参数绑定 + LIKE 字面转义）. */
function buildFacetSql(innerFrom: string, limit: number, search: string): string {
  const searched = likePattern(search) ? ` AND v ILIKE $key ESCAPE '\\'` : '';
  return [
    'SELECT v, count(*) AS c',
    `FROM (${innerFrom})`,
    `WHERE v IS NOT NULL${searched}`,
    'GROUP BY v',
    'ORDER BY c DESC, v ASC',
    `LIMIT ${limit}`,
  ].join('\n');
}

/** facet 聚合条目（值/key 名 + 估计行数）. */
export interface FacetEntry {
  name: string;
  count: number;
}

/** 解析 facet 聚合响应（v/c 两列；列缺失返回空，兼容 mock/异常响应）. */
function parseEntries(response: AnalyticsQueryResponse): FacetEntry[] {
  const vCol = response.columns.indexOf('v');
  const cCol = response.columns.indexOf('c');
  if (vCol < 0 || cCol < 0) {
    return [];
  }

  return response.rows.map((cells) => ({ name: String(cells[vCol] ?? ''), count: Number(cells[cCol]) || 0 }));
}

export interface FacetEntries {
  entries: FacetEntry[];
  /** 条数达到 LIMIT（可能还有更多，显示 Load more） */
  truncated: boolean;
  loading: boolean;
  error: string | null;
  /** 提高 LIMIT 重查（不随 filters 变化，避免过滤反馈环） */
  loadMore: () => void;
  /** 原地重查（失败重试） */
  reload: () => void;
}

/**
 * facet 聚合查询 hook（组 key 枚举 / 值 top-N 共用）：
 * 时间范围 / denoise / resetKey 变化时重查（resetKey 变化同时回到初始 LIMIT，如搜索词变更），Load more 提高 LIMIT；
 * parameters（如搜索 $key）经 ref 透传服务端绑定，不进依赖（身份每渲染变化）；from/to 未就绪时空闲.
 */
export function useFacetEntries(args: {
  domain: ChartSource;
  from: string | null;
  to: string | null;
  denoise: boolean;
  /** false 时不发查询（如未展开的 key 值列表懒加载） */
  enabled?: boolean;
  /** SQL 构建器（limit 由 hook 传入；闭包经 ref 每次渲染更新，无需 memo） */
  buildSql: (limit: number) => string;
  /** 复位 key：变化时重查并回到初始 LIMIT（组 id / 字段名 / 搜索词） */
  resetKey: string;
  /** 命名参数字典（$key 等，服务端 DuckDBParameter 绑定） */
  parameters?: SearchSqlParameters;
}): FacetEntries {
  const { domain, from, to, denoise, enabled = true, buildSql, resetKey, parameters } = args;
  const [entries, setEntries] = useState<FacetEntry[]>([]);
  const [truncated, setTruncated] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [limit, setLimit] = useState(FACET_INITIAL_LIMIT);
  const [nonce, setNonce] = useState(0);
  const buildRef = useRef(buildSql);
  buildRef.current = buildSql;
  const paramsRef = useRef(parameters);
  paramsRef.current = parameters;

  // resetKey（组 id / 字段 / 搜索词）变化时回到初始 LIMIT 再重查
  useEffect(() => {
    setLimit(FACET_INITIAL_LIMIT);
  }, [resetKey]);

  useEffect(() => {
    const sql = enabled && from && to ? buildRef.current(limit) : '';
    if (!enabled || !from || !to || !sql.trim()) {
      setEntries([]);
      setLoading(false);
      setError(null);
      setTruncated(false);
      return;
    }

    let cancelled = false;
    setEntries([]);
    setLoading(true);
    setError(null);
    searchSql({ sql, from, to, rowLimit: 10_000, parameters: paramsRef.current })
      .then((response) => {
        if (cancelled) {
          return;
        }

        setEntries(parseEntries(response));
        setTruncated(response.rows.length >= limit);
      })
      .catch((err) => {
        if (cancelled) {
          return;
        }

        setEntries([]);
        setError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [domain, from, to, denoise, enabled, resetKey, limit, nonce]);

  return {
    entries,
    truncated,
    loading,
    error,
    loadMore: () => setLimit((prev) => prev + FACET_MORE_STEP),
    reload: () => setNonce((prev) => prev + 1),
  };
}

/** 输入防抖（服务端搜索每 keystroke 重查，延迟合并）. */
export function useDebounced<T>(value: T, delay = 200): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);
  return debounced;
}
