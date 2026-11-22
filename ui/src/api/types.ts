/** 集成凭据列表不包含秘密或哈希. */
export interface ApiKeyItem {
  id: string;
  kind: 'ingestion' | 'agent';
  name: string;
  prefix: string;
  canReveal: boolean;
  enabled: boolean;
  createTime: string;
  createUserId: string;
  updateTime: string | null;
  updateUserId: string;
}
export interface ManageApiKey {
  kind: ApiKeyItem['kind'];
  action: 'create' | 'rotate' | 'enable' | 'delete' | 'reveal';
  id?: string;
  name?: string;
  enabled?: boolean;
}
export interface ApiKeySecretResponse { item: ApiKeyItem; secret: string }

/** 通知渠道；雪花 id 以字符串传输. */
export interface NotificationChannel {
  id: string;
  name: string;
  type: string;
  enabled: boolean;
  version: number;
  config: Record<string, string>;
  configuredSecrets: string[];
  createTime: string;
  updateTime: string;
  createUserId: string;
  updateUserId: string;
}

export interface SaveNotificationChannel {
  id?: string;
  version: number;
  name: string;
  type: string;
  enabled: boolean;
  config: Record<string, string>;
  keepSecrets: string[];
}

export interface NotificationSendResult {
  success: boolean;
  message: string;
}

/** 分页请求. */
export interface PagedQuery {
  page?: number;
  pageSize?: number;
}

/** 分页响应. */
export interface PagedResult<T> {
  page: number;
  pageSize: number;
  count: number;
  data: T[];
}

/** RUM 项目及采集来源配置. */
export interface RumProjectItem {
  id: number;
  projectKey: string;
  name: string;
  domainsJson: string;
  perfSampleRate: number;
  replaySampleRate: number;
  createTime: string;
  updateTime: string;
}

export interface SaveRumProjectCommand {
  id?: number;
  projectKey: string;
  name: string;
  domainsJson: string;
  perfSampleRate: number;
  replaySampleRate: number;
}

/** 系统角色：超级管理员 / 可写 / 只读. */
export type SDataRole = 'admin' | 'write' | 'readonly';

/** 登录响应. */
export interface LoginResponse {
  token: string;
  expireTime: string;
  userId: number;
  username: string;
  role: string;
  rsaPublic: string;
}

/** 当前用户. */
export interface CurrentUser {
  id: number;
  username: string;
  role: string;
}

/** 日志项. */
export interface LogItem {
  timestamp: string;
  service: string;
  severity: string;
  body: string;
  traceId?: string | null;
  spanId?: string | null;
  attributes?: string | null;
  resource?: string | null;
}

/** Trace 概要. */
export interface TraceSummaryItem {
  traceId: string;
  service: string;
  rootName: string;
  spanCount: number;
  errorCount: number;
  start: string;
  durationUs: number;
}

/** Span 详情. */
export interface SpanItem {
  traceId: string;
  spanId: string;
  parentSpanId?: string | null;
  timestamp: string;
  durationUs: number;
  service: string;
  name: string;
  kind: string;
  status: string;
  attributes?: string | null;
  events?: string | null;
  resource?: string | null;
}

/** 指标序列点. */
export interface MetricPoint {
  timestampMs: number;
  value: number | null;
}

/** 指标序列. */
export interface MetricSeriesItem {
  name: string;
  labels: string;
  points: MetricPoint[];
}

/** 遥测序列点. */
export interface TelemetryPoint {
  timestampMs: number;
  value: number | null;
}

/** 遥测序列. */
export interface TelemetrySeriesItem {
  signal: string;
  labels: string;
  points: TelemetryPoint[];
}

/** 设备. */
export interface DeviceItem {
  id: number;
  deviceKey: string;
  name: string;
  group: string;
  tagsJson: string;
  enabled: boolean;
  createTime: string;
  updateTime: string;
}

/** MQTT 接入策略；响应不包含 key 或哈希. */
export interface MqttAuthSettings {
  mode: 'none' | 'shared' | 'prefix' | 'invalid';
  hasKey: boolean;
}
export interface SaveMqttAuthSettings {
  mode: 'none' | 'shared' | 'prefix';
  key: string;
}

/** 设备最新值. */
export interface DeviceLatestValue {
  signal: string;
  numValue: number | null;
  strValue?: string | null;
  timestampMs: number;
}

/** 仪表盘. */
export interface DashboardItem {
  id: number;
  name: string;
  kind: 'view';
  description: string;
  configJson: string;
  createTime: string;
  updateTime: string;
}

/** 面板序列点. */
export interface PanelSeriesPoint {
  timestampMs: number;
  value: number | null;
}

/** 面板时间序列. */
export interface PanelSeries {
  name: string;
  /** 分组标签（JSON 对象字符串）. */
  labels: string;
  points: PanelSeriesPoint[];
}

/** 面板数据（series 非空 = 时间序列面板；否则 columns/rows 表格）. */
export interface PanelData {
  id: string;
  nodes?: CustomPanelNodeData[];
  from?: string;
  to?: string;
  truncated?: boolean;
  series: PanelSeries[];
  columns: string[];
  rows: unknown[][];
  elapsedMs: number;
  error?: string | null;
}

/** 面板图表类型. */
export type PanelType = 'line' | 'area' | 'bar' | 'stat' | 'table' | 'pie' | 'heatmap' | 'markdown' | 'group';

/** 面板查询形态：metric 原生指标 / dsl 日志·链路聚合 / promql PromQL / sql 分析 SQL. */
export type PanelQueryKind = 'metric' | 'dsl' | 'promql' | 'sql' | 'custom';

export interface CustomNodeEditor {
  version: 1;
  mode: 'builder' | 'guided';
  source: import('./chartTypes').ChartSource;
  shape: 'table' | 'scalar' | 'series' | 'category';
  aggregation: import('./chartTypes').AggFunc;
  field: string;
  where: string;
  groupBy: string[];
  granularity: import('./chartTypes').Granularity;
  metricName?: string;
  metricFilters?: { field: string; value: string }[];
  operation?: 'sum' | 'difference' | 'ratio' | 'percentage' | 'reduce';
  left?: string;
  right?: string;
}
export interface CustomPanelNode {
  ref: string;
  alias?: string;
  editor?: CustomNodeEditor;
  autoGranularity?: boolean;
  kind: 'sql' | 'expression';
  sql: string;
  parameters: SearchSqlParameters;
  shape: 'table' | 'scalar' | 'series' | 'category';
  valueField: string;
  timeField: string;
  groupFields: string[];
  expression: string;
  reduce: '' | 'sum' | 'avg' | 'min' | 'max' | 'last' | 'count';
}
export interface CustomPanelDefinition { version: 1; nodes: CustomPanelNode[]; outputs: string[] }
export interface CustomPanelNodeData { ref: string; columns: string[]; rows: unknown[][]; elapsedMs: number; truncated: boolean; error?: string }

/** DSL 过滤条件（与后端 QueryFilter 对齐）. */
export interface PanelDslFilter {
  field: string;
  op: string;
  value: unknown;
}

/** DSL 聚合定义. */
export interface PanelDslAggregation {
  func: string;
  field?: string | null;
  q?: number | null;
  alias?: string;
}

/** DSL 时间分桶. */
export interface PanelDslBucket {
  step: string;
  func: string;
  field?: string | null;
  q?: number | null;
}

/** DSL 查询体（与后端 QueryDslModel 对齐的面板子集；range 由仪表盘时间覆盖）. */
export interface PanelDsl {
  filters?: PanelDslFilter[];
  groupBy?: string[];
  aggregations?: PanelDslAggregation[];
  bucket?: PanelDslBucket | null;
}

/** 面板查询配置（kind 决定生效字段，与后端 PanelQueryConfig 对齐）. */
export interface PanelQuery {
  kind: PanelQueryKind;
  custom?: CustomPanelDefinition;
  // metric
  name?: string;
  labels?: Record<string, string>;
  aggregation?: string;
  // metric / promql
  stepSeconds?: number;
  maxSeries?: number;
  // dsl
  domain?: string;
  dsl?: PanelDsl | null;
  // promql
  expr?: string;
  // sql
  sql?: string;
  queryId?: number;
  parameters?: SearchSqlParameters;
  autoGranularity?: boolean;
  numberTrend?: import('./chartTypes').NumberTrendQuery;
}

/** 面板布局（12 列栅格）. */
export interface PanelLayout {
  /** 宽度（1–12）. */
  w: number;
  /** v2 网格高度（每行 96px、行距 12px）. */
  h: number;
  x?: number;
  y?: number;
}

/** 面板配置（与后端 PanelConfig 对齐；query 为空时按旧协议 sql/queryId 处理）. */
export interface PanelConfig {
  id: string;
  title: string;
  type: PanelType;
  panels?: PanelConfig[];
  defaultPanelId?: string;
  layout?: PanelLayout;
  unit?: string;
  showLegend?: boolean;
  markdown?: string;
  visualization?: import('./chartTypes').PanelVisualization;
  editor?: import('./chartTypes').ChartEditorDefinition;
  explorer?: import('./chartTypes').ExplorerConfig;
  query?: PanelQuery | null;
  // 旧协议字段（兼容保留）
  sql?: string;
  queryId?: number;
  rowLimit?: number;
}

/** 仪表盘时间范围配置. */
export interface DashboardTimeConfig {
  preset: string;
  from?: string;
  to?: string;
}

/** 仪表盘配置（configJson 反序列化结构，version=1 起新协议）. */
export interface DashboardConfig {
  [key: string]: unknown;
  version: number;
  time?: DashboardTimeConfig | null;
  refreshSeconds: number;
  panels: PanelConfig[];
}

/** 保存的查询. */
export interface SavedQueryItem {
  id: number;
  exactId?: string;
  name: string;
  /** 类型：sql = 分析 SQL；search = 统一搜索快照. */
  kind: string;
  /** SQL（kind=search 时为空）. */
  sqlText: string;
  /** 搜索状态 JSON（kind=search 时有效：domain/query/rangeMs）. */
  definition: string;
  description: string;
  createTime: string;
  updateTime: string;
  createUserId?: number;
  updateUserId?: number;
}

/** 统一搜索数据域（与后端 DataDomain 天表族对应）. */
export type SearchDomain = 'logs' | 'traces';

/** 保存搜索快照（saved_query.definition 反序列化结构）. */
export interface SavedSearchDefinition {
  domain: SearchDomain;
  filters?: { id: number; field: string; op: 'eq' | 'neq' | 'in' | 'nin' | 'exists' | 'nexists' | 'prefix' | 'contains' | 'root' | 'gt' | 'gte' | 'lt' | 'lte'; value: string; values?: string[] }[];
  denoise?: boolean;
  /** 查询语法模式（lucene = 属性搜索表达式；sql = 手写 WHERE 片段；缺省按 sql/query 字段回退）. */
  mode?: 'lucene' | 'sql';
  /** 行2 条件：Lucene 表达式（mode=lucene）或手写 SQL WHERE 片段（mode=sql）. */
  where?: string;
  /** 显示字段（逗号分隔，行1 关键字条两种语法共用）. */
  fields?: string;
  /** 排序表达式（如 `ts DESC`，行1 关键字条两种语法共用）. */
  orderBy?: string;
  /** 行数上限（两种语法共用）. */
  limit?: number;
  /** 历史整句搜索 SQL（旧版 SQL 模式快照；打开时回退提取 WHERE 片段）. */
  sql?: string;
  /** 旧版自定义语法快照（仅历史数据；运行将得到 SQL 校验错误，需改写）. */
  query?: string;
  /** 相对时间窗毫秒数. */
  rangeMs: number;
}

export interface AlertDraft {
  id?: string;
  version: number;
  savedQueryId?: string;
  name: string;
  domain: SearchDomain;
  definition: string;
  condition: string;
  parameters: SearchSqlParameters;
  channels: string[];
  note: string;
  operator: 'gte' | 'gt' | 'lte' | 'lt';
  threshold: number;
  windowMinutes: number;
  delayMinutes: number;
  consecutiveWindows: number;
  enabled: boolean;
}

export interface AlertItem extends AlertDraft {
  id: string;
  status: string;
  error: string;
  createTime: string;
  updateTime: string;
  createUserId: string;
  updateUserId: string;
}

export interface AlertPoint { time: string; count: number; lowerBound: boolean; status: string; error: string }
export interface AlertHistory { points: AlertPoint[]; deliveries: { channelId: string; status: string; attempts: number; result: string }[] }
export interface AlertChannelItem { id: string; name: string; type: string; enabled: boolean }

/** 搜索查询命名参数（SQL 中 $name 引用的绑定值；$start/$end 由 from/to 提供）. */
export type SearchSqlParameters = Record<string, string | number | boolean | null>;

/** 统一搜索 SQL 直查请求（POST /api/analytics/search）. */
export interface ExecuteSearchSqlRequest {
  /** 搜索 SQL（{{logs}}/{{traces}} 表占位符 + $start/$end/$pN 时间与命名参数）. */
  sql: string;
  /** 起始时间（含，ISO 8601）. */
  from: string;
  /** 结束时间（不含，ISO 8601）. */
  to: string;
  /** 行数上限（1..10000）. */
  rowLimit?: number;
  /** 命名参数字典（$p0..$pN 引用的绑定值，服务端以 DuckDBParameter 绑定）. */
  parameters?: SearchSqlParameters;
}

/** 统一查询 DSL 过滤（op：eq/neq/in/prefix/match/gt/gte/lt/lte）. */
export interface QueryDslFilter {
  field: string;
  op: 'eq' | 'neq' | 'in' | 'prefix' | 'match' | 'gt' | 'gte' | 'lt' | 'lte';
  value: string | number | string[];
}

/** 统一查询 DSL 时间分桶直方图. */
export interface QueryDslBucket {
  step: string;
  func?: 'count' | 'avg' | 'sum' | 'min' | 'max' | 'quantile';
  field?: string;
  q?: number;
}

/** 统一查询 DSL 排序. */
export interface QueryDslSort {
  field: string;
  desc: boolean;
}

/** 统一查询 DSL 请求（POST /api/{logs|traces}/search）. */
export interface QueryDslRequest {
  range: { from: string; to: string };
  filters?: QueryDslFilter[];
  groupBy?: string[];
  aggregations?: { func: string; field?: string; q?: number; alias?: string }[];
  bucket?: QueryDslBucket;
  sort?: QueryDslSort[];
  limit?: number;
}

/** 统一查询 DSL 响应（列式：行键=列名）. */
export interface SearchDslResponse {
  columns: string[];
  rows: Record<string, unknown>[];
  took: number;
  tablesScanned: number;
}

/** DuckDB 实际表结构，不含 SQLite 应用元数据. */
export interface AnalyticsTableItem {
  schema: string;
  name: string;
  domain: string | null;
  day: string | null;
  columns: { name: string; type: string; nullable: boolean }[];
}

/** 分析查询结果. */
export interface AnalyticsQueryResponse {
  columns: string[];
  rows: unknown[][];
  elapsedMs: number;
  truncated: boolean;
}

/** 用户项（用户管理列表）. */
export interface UserItem {
  id: number;
  username: string;
  role: string;
  createTime: string;
}

/** 服务状态. */
export interface MqFailureItem {
  id?: string;
  actualTopic?: string | null;
  stage: string;
  code: string;
  reason: string;
  at: string;
  recoveredAt?: string | null;
  occurrences: number;
}

export interface MqTopicRuntimeItem {
  stage?: string;
  stageSince?: string | null;
  pendingPersistence?: number;
  pendingConfirmation?: number;
  lastProgressAt?: string | null;
  failures?: MqFailureItem[];
  name: string;
  matchMode: string;
  state: string;
  matchedTopics: number;
  receivedMessages: number;
  receivedBytes: number;
  persistedRecords: number;
  parseFailures: number;
  blockedMessages?: number;
  blockedSince?: string | null;
  confirmedMessages: number;
  confirmationFailures: number;
  messagesPerSecond: number;
  recordsPerSecond: number;
  bytesPerSecond: number;
  lastPersistedAt?: string | null;
  lastError?: string | null;
}

export interface MqConsumptionResponse {
  notifications?: { instanceId: number; topic: string; channelId: string; suppressedUntil?: string | null; suppressedCount: number; status: string }[];
  writerFailure?: { code: string; reason: string; at: string; recoveredAt?: string | null } | null;
  sampledAt: string;
  windowSeconds: number;
  instances: MqRuntimeItem[];
}

export interface MqRuntimeItem {
  retryAttempt?: number;
  nextRetryAt?: string | null;
  instanceId: number;
  sessionId?: string;
  topics?: MqTopicRuntimeItem[];
  state: string;
  persistedRecords: number;
  lastPersistedAt?: string | null;
  lastError?: string | null;
}

export interface ResourceSnapshot {
  state: string;
  sampledAt?: string | null;
  os: string;
  osArchitecture: string;
  processArchitecture: string;
  runtime: string;
  processorCount: number;
  systemCpuPercent?: number | null;
  processCpuPercent?: number | null;
  totalMemoryBytes?: number | null;
  availableMemoryBytes?: number | null;
  workingSetBytes?: number | null;
  privateMemoryBytes?: number | null;
  gcHeapBytes?: number | null;
  processThreads?: number | null;
  threadPoolThreads?: number | null;
  pendingWorkItems?: number | null;
  containerMemoryLimitBytes?: number | null;
  containerMemoryBytes?: number | null;
  containerCpuLimit?: number | null;
}

export interface LocalStorageSnapshot {
  state: string;
  sampledAt?: string | null;
  duckDbState: string;
  duckDbSampledAt?: string | null;
  fileSampledAt?: string | null;
  trackedMemoryBytes?: number | null;
  temporaryBytes?: number | null;
  memoryLimit?: string | null;
  threads?: number | null;
  fileState: string;
  databaseBytes?: number | null;
  walBytes?: number | null;
  disks: { volume: string; state: string; totalBytes?: number | null; availableBytes?: number | null }[];
}

export interface S3StorageSnapshot {
  state: string;
  checkedAt?: string | null;
  sampledAt?: string | null;
  bucket: string;
  prefix: string;
  bytes?: number | null;
  objects?: number | null;
}

export interface StatusResponse {
  resources?: ResourceSnapshot;
  mqConsumers?: MqRuntimeItem[];
  name: string;
  version: string;
  uptimeSeconds: number;
  processMemoryBytes: number;
  gcHeapBytes: number;
  threadCount: number;
  os: string;
  buffer: {
    buffered: Record<string, number>;
    processed: Record<string, number>;
    dropped: Record<string, number>;
  } | null;
  tableCounts: Record<string, number>;
  todayRows: Record<string, number>;
  duckDbMemoryLimit: string;
}

/** 接入通道健康项（总览页）. */
export interface OverviewChannel {
  name: string;
  processed: number;
  dropped: number;
  buffered: number;
  healthy: boolean;
}

/** 存储用量（总览页）. */
export interface OverviewStorage {
  hotDbBytes: number;
  hotDbPath: string;
  coldEnabled: boolean;
  memoryLimit: string;
}

export interface OverviewMqConsumer extends MqRuntimeItem {
  name: string;
  type: string;
  enabled: boolean;
}

/** 总览页聚合响应（通道健康/今日量/存储用量）. */
export interface OverviewResponse {
  todayRows: Record<string, number>;
  tableCounts: Record<string, number>;
  channels: OverviewChannel[];
  storage: OverviewStorage;
  localResources?: LocalStorageSnapshot;
  s3Resources?: S3StorageSnapshot;
  bufferedRecords: number;
  mqConsumers: OverviewMqConsumer[];
}

/** RUM 性能汇总. */
export interface RumPerformanceResponse {
  pageViews: number;
  uniqueVisitors: number;
  vitalP75: Record<string, number>;
}

/** RUM 错误聚合项. */
export interface RumErrorAggregate {
  name: string;
  count: number;
}

/** RUM 会话摘要. */
export interface RumSessionSummary {
  sessionId: string;
  firstSeen?: string;
  lastSeen: string;
  pageViews?: number;
  errorCount?: number;
  eventCount?: number;
  hasReplay?: boolean;
  pagePath?: string;
  userAgent?: string;
  domain?: string;
}

/** RUM 会话回放分段. */
export interface RumReplayChunk {
  seq: number;
  timestamp: string;
  blobBase64: string;
}

/** RUM 会话回放响应. */
export interface RumReplayResponse {
  sessionId: string;
  chunks: RumReplayChunk[];
}

/** RUM 热力栅格单元（T615）. */
export interface RumHeatmapCell {
  x: number;
  y: number;
  count: number;
}

/** RUM 点击目标聚合项（T616；selector 优先，回退 targetId）. */
export interface RumClickTarget {
  target: string;
  count: number;
}

/** RUM 页面树节点（T615）. */
export interface RumPageNode {
  pagePath: string;
  clicks: number;
  errors: number;
}

/** RUM 实时点击推送（/hub/rum，T616）. */
export interface RumLiveClick {
  projectKey: string;
  sessionId: string;
  pagePath?: string;
  x?: number;
  y?: number;
  pageX?: number;
  pageY?: number;
  docW?: number;
  docH?: number;
  selector?: string;
  timestamp: string;
}

/** S3 冷存储设置（SecretKey 掩码返回，空/掩码提交 = 沿用已存值）. */
export interface S3Settings {
  endpoint: string;
  bucket: string;
  prefix: string;
  accessKey: string;
  secretKey: string;
  hasSecret: boolean;
}

/** 单域保留档位（热/冷天数，0 = 永久/不转冷）. */
export interface RetentionTier {
  hotDays: number;
  coldDays: number;
}

/** 三段保留计划（5 域）. */
export interface RetentionSettings {
  logs: RetentionTier;
  traces: RetentionTier;
  metrics: RetentionTier;
  iot: RetentionTier;
  rum: RetentionTier;
}

/** 数据压缩设置（冷存档 parquet 压缩编码）. */
export interface CompressionSettings {
  archiveParquetCodec: string;
}

/** MQ 解析规则参数（按 format 取用：regex 用 pattern，csv 用 delimiter，其余用字段映射）. */
export interface MqParserOptions {
  timestampField: string;
  timestampFormat: string;
  severityField: string;
  severityMap?: Record<string, string> | null;
  serviceField: string;
  bodyField: string;
  traceIdField: string;
  spanIdField: string;
  pattern: string;
  delimiter: string;
  keepUnmapped: boolean;
}

/** MQ topic/队列消费配置（解析规则 + 默认 service/severity）. */
export interface MqTopic {
  name: string;
  matchMode?: 'exact' | 'prefix';
  format: string;
  service: string;
  severity: string;
  parser?: MqParserOptions | null;
}

/** MQ 日志消费实例（多实例；保存后由消费管理器自动热更新，无需重启）. */
export interface MqInstance {
  retryInitialSeconds?: number;
  retryMaxSeconds?: number;
  alertsEnabled?: boolean;
  notificationChannelIds?: string[];
  alertCooldownMinutes?: number;
  id: number;
  name: string;
  type: string;
  enabled: boolean;
  bootstrapServers: string;
  securityProtocol?: string;
  saslMechanism?: string;
  saslUsername?: string;
  saslPassword?: string;
  groupId: string;
  autoOffsetReset: string;
  uri: string;
  serviceUrl: string;
  subscriptionName: string;
  subscriptionType: string;
  adminUrl?: string;
  adminUsername?: string;
  adminPassword?: string;
  namespace?: string;
  discoveryIntervalSeconds?: number;
  topics: MqTopic[];
}

/** MQ 日志解析预览请求（样例文本 + 解析规则）. */
export interface MqParsePreviewRequest {
  format: string;
  service: string;
  severity: string;
  parser?: MqParserOptions | null;
  sample: string;
}

/** MQ 日志解析预览单条结果. */
export interface MqPreviewRecord {
  timestamp: string;
  severity: string;
  service: string;
  body: string;
  traceId?: string | null;
  spanId?: string | null;
  attributesJson?: string | null;
}

/** MQ 日志解析预览响应（存在解析失败行时 success=false，看 error 与 attributes 的 _parse_error）. */
export interface MqParsePreviewResult {
  success: boolean;
  error?: string | null;
  records: MqPreviewRecord[];
}

/** 自身遥测导出设置（重启生效）. */
export interface OtlpSettings {
  trace: string;
  metrics: string;
  protocol: string;
  traceHeaders?: string;
  metricsHeaders?: string;
}

export interface SaveOtlpSettings extends OtlpSettings {
  clearTraceHeaders?: boolean;
  clearMetricsHeaders?: boolean;
}

/** 摄取缓冲参数（启动时绑定，只读展示）. */
export interface IngestSettings {
  requireAuth: boolean;
  bufferCapacity: number;
  batchSize: number;
  flushIntervalMs: number;
}

/** 启动参数快照（只读；可在 configs/system.json 覆盖，重启生效）. */
export interface StartupSettings {
  systemJsonExists: boolean;
  port: number;
  mqttPort: number;
  webUi: boolean;
  databasePath: string;
  duckDbPath: string;
  duckDbMemoryLimit: string;
  duckDbCheckpointThreshold: string;
  duckDbQueryCacheTtlSeconds: number;
  duckDbQueryCacheMaxEntries: number;
  duckDbTempDirectory?: string | null;
  ingestRequireAuth: boolean;
  ingestBufferCapacity: number;
  ingestBufferByteBudgetBytes: number;
}

/** DuckDB 内存设置（保存后立即生效）. */
export interface DuckDbSettings {
  memoryLimit: string;
  effectiveMemoryLimit: string;
}

/** 系统设置响应（GET /settings，管理员）. */
export interface SettingsResponse {
  /** 旧版后端尚未提供该字段；缺失时禁用内存配置入口. */
  duckDb?: DuckDbSettings | null;
  s3: S3Settings;
  retention: RetentionSettings;
  compression: CompressionSettings;
  otlp: OtlpSettings;
  /** 旧版后端尚未提供该字段；缺失时禁用启动配置入口. */
  startup?: StartupSettings | null;
  ingest: IngestSettings;
}

/** S3 连接测试响应. */
export interface TestS3ConnectionResponse {
  success: boolean;
  message: string;
}
