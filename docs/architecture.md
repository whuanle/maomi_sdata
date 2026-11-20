# 功能架构总览

## 关联

- 根规范：`AGENTS.md`（技术栈、存储两线、CQRS、验证）
- 存储细节：[storage-layout.md](storage-layout.md)
- 模块分层：[cqrs-conventions.md](cqrs-conventions.md)

## 定位与总原则

内网单机部署的观测/数据分析平台，**单容器、零外部服务依赖**（README 明文承诺：不依赖 ES/PG/Redis/S3 等；S3 为可选外置冷存储，无 S3 时功能完整）。由此推导四条原则：

1. **一个进程承载一切**：HTTP API、gRPC、MQTT broker、探针上报、UI 全在宿主进程内；MQTT/S3 是内嵌组件或可选项，不是外部系统。
2. **摄取只进缓冲**：任何协议适配器（OTLP/ES/MQTT/RUM）都终结于 `IIngestBuffer`，协议层只做解析与鉴权，不做存储。
3. **兼容主流协议的写入口，不自造语言**：查询侧复用业界协议（结构化 DSL / SQL / PromQL 子集），写入口兼容 OTLP 与 ES Bulk。
4. **磁盘分层**：本地盘是热层，对象存储是冷层，都是可选项；每一层关闭时系统照常工作。

## 总体拓扑

```
                         ┌─────────────────────── 单进程 SData 宿主 (Kestrel:5300, HTTP1+2) ───────────────────────┐
 OTel SDK/Collector ────▶│ OTLP gRPC (/v1/*)  OTLP HTTP protobuf+gzip+JSON (/v1/*)                                │
 Filebeat/Logstash ─────▶│ ES Bulk (/_bulk, NDJSON, Basic auth)                                                   │
 浏览器探针 SDK ─────────▶│ RUM Beacon (/rum/v1/beacon)                          协议适配层 = 解析 + 鉴权            │
 REST/前端控制台 ────────▶│ MVC API (/api/*) + UI 静态资源 + PromQL 子集(二期 /api/v1/query)                        │
                         └──────────────┬─────────────────────────────────────────────────────────────────────────┘
 MQTT 设备 ──(TCP:1883)────▶ MQTTnet 内嵌 broker（Iot 域）       │
                                        ▼                        ▼
                            IIngestBuffer（有界 Channel，满则丢弃/429 背压）
                                        ▼ IngestWriterService（单读线程，Appender 批量）
                            DuckDB 单实例（热层，按天分表）──ArchiveService──▶ S3/MinIO Parquet（冷层）
                                        ▲                                              ▲
                            结构化查询 DSL ──编译──▶ 参数化 SQL ──ITableResolver── 本地表 ∪ read_parquet(s3://)
SQLite：用户/设备/仪表盘/RUM 项目/接入 Key/冷存台账
```

## 决策 1：DuckDB 单实例（定案）

**结论：一个 DuckDB 实例、一个主库文件（`data/sdata.duckdb`）、单进程。不做多实例。**

理由：

- DuckDB 是进程内引擎，库文件同一时刻只能被一个进程可写打开——多实例必然多进程，跨域查询（日志↔链路 trace_id 关联、大屏混采）被迫退化为应用层聚合，代价最大。
- 写吞吐瓶颈在 Appender 批量与缓冲，不在库数量；现有 `IngestWriterService` 单写线程 + `SemaphoreSlim` 写锁模型对单机够用。
- 多实例解决不了单实例的真实风险，那些风险另有对策：
  - **checkpoint 停顿/恢复时间**：批量写入 + `RetentionService` 后 CHECKPOINT（已有）；必要时调 `memory_limit`/`threads`。
  - **单文件膨胀**：见下方逃生舱。

**逃生舱（预留，不实施）**：同一连接可 `ATTACH` 多个库文件。若单文件 checkpoint/恢复不可接受，按年拆 `sdata-2026.duckdb` 附加文件——查询层本来就按表名路由，改造成本局部化。在此之前不做。

**明确否决**：多进程多实例（文件锁互斥 + 跨域 join 失效 + 内存翻倍 + 运维复杂，与单机定位冲突）。

## 决策 2：摄取接入协议矩阵

| 协议 | 客户端 | 路径/端口 | 格式 | 鉴权 |
|---|---|---|---|---|
| OTLP gRPC | OTel SDK / Collector | `POST /v1/logs|traces|metrics`（5300 同端口 h2c；可选别名监听 4317） | protobuf（支持 gzip） | `X-Api-Key` 或 `Authorization: Bearer`（IngestKey） |
| OTLP HTTP | OTel SDK / Collector | 同上路径（可选别名监听 4318） | protobuf + JSON（`Content-Encoding: gzip` 均支持） | 同上 |
| ES Bulk | Filebeat / Logstash / Vector | `POST /_bulk`、`POST /{index}/_bulk`（可选别名监听 9200） | NDJSON（`application/x-ndjson`） | Basic auth（username 任意，password = IngestKey；Filebeat 原生支持） |
| 自有 JSON | 平台自身/脚本 | `POST /api/ingest/logs|traces|metrics|iot`（现状保留） | JSON 批量 | `X-Api-Key` |
| MQTT | IoT 设备 | TCP 1883（可配） | 主题约定见下 | username=DeviceKey，password=DeviceSecret |
| RUM Beacon | 浏览器探针 | `POST /rum/v1/beacon` | JSON 批量（gzip） | 公开 ProjectKey（非机密）+ CORS + 采样限速 |

约定：

- **OTLP 走标准路径** `/v1/*`（无 `/api` 前缀），现有 `/api/ingest/otlp/v1/*` 保留为别名一个版本周期后废弃。protobuf 解码用官方 `opentelemetry-proto` 生成的 C# 类型（`Grpc.AspNetCore` 宿主服务）；Kestrel 单端口 `Http1AndHttp2` 同时服务 MVC 与 gRPC（OTel 客户端走 prior-knowledge h2c）。
- **ES Bulk 只做写入兼容，不做 `_search`**。支持 `index`/`create` 动作；`update`/`delete` 返回明确的 ES 形状错误。响应必须是 ES 形状（`took/errors/items[].status`），缓冲满时返回 **429**——Filebeat/Logstash 会自动重试，天然背压。索引名写入 `logs_yyyymmdd.source` 列（流标识），查询 DSL 可按它过滤。
- **MQTT**：内嵌 MQTTnet broker，只做 broker（无桥接/集群/共享订阅）。主题两种形态：`iot/{deviceKey}/{signal}`（单值，payload 为数值或 `{ts,num,str,quality}` JSON）与 `iot/{deviceKey}`（payload 为批量 JSON 数组）。QoS 支持 0/1。ACL：设备只能发布自身 deviceKey 子树。`device.last_seen_time` 批量回写（每 N 秒一次），禁止逐消息写 SQLite。
- **RUM**：见决策 4。

所有协议适配器的落点都是 `IIngestService` → `IIngestBuffer`，缓冲满的策略统一：HTTP 通道返回 429、MQTT 按主题 QoS 丢弃并计数、RUM 直接丢弃计数（打点已有 `sdata_ingest_dropped_total`）。

## 决策 3：存储分层与 S3 冷存储

**三层生命周期**（按域配置，替代现有单一 `Retention:{Domain}`）：

```
热层（本地 DuckDB 天表，HotDays）──ArchiveService──▶ 冷层（S3 Parquet，ColdDays）──▶ 过期（删除对象）
```

- **归档**：`ArchiveService`（Store 域新 BackgroundService，与 `RetentionService` 并列）。到期天表 → `COPY (SELECT * FROM logs_yyyymmdd) TO 's3://bucket/sdata/logs/20261015/*.parquet' (FORMAT PARQUET, COMPRESSION zstd)` → 行数校验 → 记台账 → DROP 本地表 + CHECKPOINT。S3 协议兼容，**内网 MinIO 即可用**；httpfs 由 DuckDB.NET.Full 内置（M5 首个任务验证，缺失则补动态加载，冷存储配置为空时整条链路不启用、不加载扩展）。
- **台账**：SQLite 新表 `cold_archive`（domain、day、object_key、row_count、归档时间），幂等重试不重复上传，也是"哪些日期在冷层"的展示依据。
- **查询透明化**：`TimeTables` 升级为 `ITableResolver.Resolve(domain, range) → {本地表[], 冷层 parquet glob[]}`；各 Store 查询在现有 `UNION ALL` 上追加 `read_parquet('s3://...')` 分支。glob 由内部日期拼出，**不是用户输入**（沿用参数绑定红线；冷存储凭证经连接级 `CREATE SECRET` 注入，用户 SQL 通道 `IAnalyticsExecutor` 已禁 `set/secret/attach/copy/install/load`，摸不到）。
- **指标特殊**：原始样本短保留 + 预聚合长保留（对齐 Prometheus 生态习惯）：新增 `metrics_rollup_5m` / `metrics_rollup_1h`（name + labels 指纹分桶的 min/max/sum/count），`RollupService` 周期回填；长区间查询（大屏 30 天趋势）走 rollup，短区间走原始表。

## 决策 4：RUM 前端探针（三阶段）

自研轻量探针（TypeScript SDK，平台下发 `<script>` 引用），**基于 rrweb（DOM 录制）+ web-vitals（性能指标）组装，不集成 OpenReplay/Sentry self-host**（违背零依赖，叠加异构技术栈）。分阶段交付，每阶段独立可用：

- **A 基础数据面**：PV/UV、SPA 路由切换、Web Vitals（LCP/INP/CLS/FCP）、JS 错误与 Promise 拒绝、资源加载错误、XHR/fetch 耗时与状态码、自定义事件 API。上报：`/rum/v1/beacon` 批量 JSON（`CompressionStream` gzip，unload 走 `sendBeacon`/keepalive）；采样率由 `/rum/v1/config` 下发。
- **B 会话回放**：rrweb 增量事件按会话分段 gzip，存 `rum_replay_yyyymmdd`（session_id、seq、blob）；回放 API 返回分段清单 + 数据，UI 用 rrweb-player 播放，与同时段日志/错误时间轴对齐。
- **C 分析面**：点击热力图（点击坐标聚合为页面×栅格桶，不存逐点）、页面站点地图树（URL 层级 + 访问量 + 加载耗时分布）、路径/漏斗分析（远期）。

存储表族：`rum_session_yyyymmdd`（会话元数据）、`rum_event_yyyymmdd`（类型化事件 + attributes）、`rum_replay_yyyymmdd`（回放 blob 段）。SQLite 新实体 `rum_project`（ProjectKey、采样配置、CORS 域名）。

## 决策 5：查询与可视化协议（不自造语言）

三层协议，按序优先：

1. **结构化查询 DSL（主协议）**：所有域统一的 JSON 查询模型——`range / filters(field,op,value) / groupBy / aggregations / sort / limit`，由 Store 域的 DSL 编译器（`Store.Core/QueryDsl/`）翻译为参数化 SQL。这是 UI 日志检索、链路瀑布、指标图表、仪表盘面板取数的唯一内部协议；现有各域查询命令向它演进（活动仪表盘仅支持 `dashboard.kind = view`；Screens 已下线，历史 screen 配置原样保留但不可通过活动 API 操作）。前端渲染统一走已有 ECharts 封装。
2. **SQL 逃生舱**：`/api/analytics`（`IAnalyticsExecutor`，只读/单语句/行数上限）保持不变，服务高级分析。
3. **PromQL 子集（二期，指标大屏用）**：不做完整 PromQL 引擎。解析子集（`rate/sum/avg/min/max/count/topk/bottomk/by/offset` 等）→ AST 翻译为 rollup/原始表 SQL；暴露 Prometheus HTTP API 形状 `/api/v1/query|query_range`——**Grafana 的 Prometheus 数据源可直接指向 SData**，大屏可直接复用 Grafana 面板定义与生态。解析器优先评估现成 .NET 移植（仅取 AST），否则 ANTLR 官方 grammar 生成。

**明确不做**：ES `_search` DSL 读取接口、完整 LogQL、自研查询语言。读日志/链路就是上面的 DSL + SQL。

## 数据与元数据增量

- DuckDB：`logs_yyyymmdd` 增 `source` 列（ES 索引名/流）；新增 `rum_*` 表族、`metrics_rollup_{5m,1h}`；表 DDL 均入 `TimeTables`/内嵌版本化 DDL，天表按日新建，列演进在启动时对近期天表 `ALTER TABLE ADD COLUMN` 修补。
- SQLite：`device` 增 `secret_hash`（MQTT 密码）；新增 `rum_project`、`cold_archive`；`ingest_key` 初期保持全局，不按渠道拆分（够用且简单）。
- 配置（`SData:`）：`Retention:{Domain}:{Hot,Cold}`（替代单一 Retention，旧键兼容映射）、`S3:{Endpoint,Bucket,AccessKey,SecretKey}`（空 = 关闭冷层）、`Mqtt:Port`（默认 1883，随宿主始终开启，无启停开关）、`Otlp:{Enabled}`、`EsBulk:{Enabled}`、`Rum:{Enabled}`。可选通道独立开关，MQTT 不在可选范围。

## 模块规划（映射到三项目规范）

| 变更 | 归属 | 内容 |
|---|---|---|
| 演进 | `SData.Ingest.*` | OTLP protobuf/gzip/gRPC 接收器（标准 `/v1/*`）、`EsBulkController`（`/_bulk`）、解析器入 `Ingest.Core/Services/` |
| 演进 | `SData.Store.*` | `ITableResolver`、`ArchiveService`、`RollupService`、`QueryDsl` 编译器、冷存储连接初始化 |
| 新模块 | `SData.Rum.{Shared,Core,Api}` | Beacon 接收、会话/回放/热力/站点地图查询、采样配置下发；探针 SDK 放 `ui/packages/rum-sdk`（独立 npm 包，构建产物托管到宿主静态资源） |
| 演进 | `SData.Iot.*` | 内嵌 MQTT broker（`Iot.Core` BackgroundService，设备鉴权用本域台账，避免跨域取凭证） |
| 演进 | `SData.Metrics.*` | 二期 PromQL 翻译器 + `/api/v1/query*` 控制器 |
| 演进 | `SData.Dashboard.*` | 普通仪表盘与面板查询编排；Screens 已下线 |

## 里程碑（按价值/成本排序，顺序可按磁盘压力与需求调整）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M1 OTLP 完整兼容 | protobuf + gzip + gRPC + 标准 `/v1/*` + Basic/Bearer 鉴权 | 官方 OTel Collector 一次配置对接成功 |
| M2 ES Bulk | `/_bulk` NDJSON、429 背压、`source` 流列 | Filebeat/Logstash 直推入库 |
| M3 查询 DSL + rollup + 仪表盘 | 统一 DSL、`metrics_rollup_*`、仪表盘面板数据协议 | 仪表盘页面跑通；30 天趋势走 rollup |
| M4 MQTT | broker + 设备鉴权 + 主题约定 + LastSeen 批量回写 | MQTTX 按约定推遥测入库 |
| M5 S3 冷存储 | ArchiveService + ITableResolver + 台账 + MinIO 验证 | 过期天表迁 S3 后查询结果无感一致 |
| M6 RUM（A→B→C） | 基础数据面 → 回放 → 热力图/站点地图 | 探针接入示范站点三阶段逐个可用 |
| M7 PromQL 子集 | AST 翻译 + Prometheus HTTP API 形状 | Grafana Prometheus 数据源出图 |

## 明确不做（Out of Scope）

- ES `_search` / 完整 LogQL 读兼容；自研查询语言。
- DuckDB 多进程实例、分布式/集群、多租户计费。
- 消息队列（Kafka/RabbitMQ）**中间层**——单机内存缓冲 + 429 背压已覆盖定位；溢出计数可见即可。注：外部 MQ 作为可选日志输入通道已支持（`SData:Kafka/RabbitMq/Pulsar`，默认关闭，见 ingest 域 sdd），但 MQ 不充当内部数据管道。
- 告警引擎（另立规划，依赖 M3 的 DSL 与 rollup）。
