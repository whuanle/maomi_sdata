# metrics — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md)

## 目标

指标查询与 Prometheus 兼容查询面：统一 DSL（C1，T310/T311）、长区间 rollup 分派（C6，T312）、PromQL 子集（T710–T712）。

## 组件

- Go：`go/internal/query/promql.go` 解析同一子集并生成绑定 SQL，单次解析天表集合，区间复用；`go/cmd/sdata/prometheus.go` 提供原 vector/matrix 与 bad_data 响应，JWT 必需。保持 C# 的 rate=(max-min)/窗口秒数、without 与 offset 的现有子集限制，不宣称完整 Prometheus 语义；单查询预算 30 秒，当前仅热表。

- `MetricsController`：既有指标查询（MetricStore 分桶聚合/序列浏览）。
- `POST /api/metrics/search`（T311）：C1 DSL，经 IQueryDslExecutor。
- `MetricsCoreModule`：注册 PromqlExecutor（T711）。
- `PromqlParser/Lexer/Ast`（T710）：手写递归下降，子集 = metric 名/四 label 匹配/rate/七聚合+by without/范围向量/offset；不支持二元运算/子查询/histogram_quantile（二期）。
- `PromqlSqlTranslator`（T711）：AST→参数化 SQL。语义约定：rate=(max-min)/range_seconds；即时=arg_max(区间末端)；by 键 AS 原标签名输出；without 降级为按序列全标签分组；冷层 read_parquet 合并；表集合由 ITableResolver 提供（>24h 查询建议走 rollup 表，见 storage-layout）。
- `PromController`（T711）：`GET /api/v1/query`、`GET /api/v1/query_range`（Prometheus JSON 形状，bad_data 错误），对接清单见 configs/grafana-note.md。

## 数据布局

metrics_{day}（name/ts/value/labels/kind）+ metrics_rollup_5m/1h（C6）。查询 >24h 的 avg/sum/min/max/count 由 MetricStore 分派 rollup（T312）；PromQL 查询当前走原始天表（rollup 分派待二期接入 PromqlExecutor——区间与步长分派逻辑与 MetricStore 一致时对齐）。

## 关键决策

- 解析选型手写零依赖（对比见 STATE.md 决策日志 T710）。
- PromqlParseException 定义在 Metrics.Shared（Api 层 Controller 捕获转 Prometheus error，保持 Api→Shared 单向）。
- Grafana 直连：URL 指 SData 根，`/api` 全局前缀与 `/api/v1/*` 兼容路径吻合（configs/grafana-note.md）。

## 已知问题

- PromQL 查询未接入 rollup 分派（长区间原始表查询受 MaxQueryDays 约束兜底）。
- without 语义降级；无 lookback delta（取窗口末端 arg_max）。

## 指标曲线展示（2026-11-18）

- 指标页复用 `design-system/TimeSeriesChart`，与仪表盘统一图例、空态、时间轴和亮暗主题；开启 inside/slider 缩放，不改查询语义。共享实现见 [dashboard SDD](../dashboard/sdd.md)。
