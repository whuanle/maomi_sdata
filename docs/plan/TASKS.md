# 任务卡与接口契约

## 关联

- [README.md](README.md) — 协议；[STATE.md](STATE.md) — 账本；[../architecture.md](../architecture.md) — 架构决策
- 规模：S ≤ 半天 / M ≈ 1 天 / L ≥ 2 天（含测试与文档）

## 契约区（C1–C6，跨任务编码依据，变更须登记 STATE.md 决策日志）

### C1 统一查询 DSL（T310 定义实现；T311/T313/T314/T510/T711 消费）

请求体（`POST /api/{domain}/search`，domain = logs|traces|metrics|iot）：

```jsonc
{
  "range": { "from": "2026-11-15T00:00:00+08:00", "to": "2026-11-15T21:00:00+08:00" },
  "filters": [ { "field": "service", "op": "eq", "value": "gateway" } ],   // op: eq|neq|in|prefix|match
  "groupBy": [ "service" ],
  "aggregations": [ { "func": "count", "alias": "total" } ],               // count|avg|sum|min|max|quantile
  "bucket": { "step": "5m", "func": "count" },                             // 可选：时间分桶直方图
  "sort": [ { "field": "total", "desc": true } ],
  "limit": 50
}
```

C# 入口（Store.Core/QueryDsl/）：`QueryDslCompiler.Compile(QueryDslModel q, DataDomain domain) → CompiledQuery { string Sql; IReadOnlyList<DuckDBParameter> Parameters; IReadOnlyList<string> Tables; }`。只生成 SELECT；一切外部值参数绑定；表名来自 C2。

### C2 ITableResolver（接口 T310 落在 Store.Shared；T510 实现冷层）

```csharp
public interface ITableResolver
{
    ResolvedTables Resolve(DataDomain domain, DateTimeOffset from, DateTimeOffset to);
}
public sealed record ResolvedTables(IReadOnlyList<string> LocalTables, IReadOnlyList<string> ColdParquetGlobs);
```

无 S3 配置时 ColdParquetGlobs 恒为空，查询层行为与现状一致。

### C3 RUM 上报协议（T610 定义；T612/T614 消费）

`POST /rum/v1/beacon`（gzip JSON）：`{ "projectKey": "rum_xxx", "sessionId": "s_ab12kp", "events": [ { "type": "pageview|vital|error|click|custom|replay-chunk", "ts": "...", ... } ] }`；采样配置 `GET /rum/v1/config?projectKey=`；成功 204，缓冲满 429，键无效 401。回放事件经 `type=replay-chunk` 分段（seq + gzip blob base64）。

### C4 配置键（T000 写入 system.example.json 注释模板；各任务按需实现读取）

```
SData:Otlp:Enabled                     # T112
SData:EsBulk:{Enabled,Port}            # T120（Port=0 表示不开别名端口，默认 9200）
SData:Mqtt:Port                       # T140（默认 1883，随宿主始终启动，无启停开关）
SData:S3:{Endpoint,Bucket,Prefix,AccessKey,SecretKey}   # T511（空 = 关闭冷层）
SData:Retention:{Logs|Traces|Metrics|Iot|Rum}:{HotDays,ColdDays}  # T512（旧 Retention:{X} 键兼容映射为 HotDays）
SData:Listeners:[ { Port, Protocol: "http"|"grpc" } ]   # T112 通用附加监听（4317/4318/9200 全部由此实现）
```

### C5 实体增量（append-only；实体文件各自新建）

- `DeviceEntity` + `SecretHash`（string，PBKDF2/SHA256 存储，T141）
- 新增 `RumProjectEntity`（Id, ProjectKey, Name, DomainsJson, PerfSampleRate, ReplaySampleRate，T611）
- 新增 `ColdArchiveEntity`（Id, Domain, Day, ObjectKey, RowCount, ArchivedTime，T512）
- `DatabaseContext` 各追加一行 DbSet

### C6 指标 rollup 表（T312）

`metrics_rollup_5m` / `metrics_rollup_1h`（同构）：`(name, labels_hash, labels, bucket_ts, min, max, sum, count)`；DDL 入 TimeTables/内嵌版本化 DDL；RollupService 周期回填（5m 表滞后 ≤10 分钟，1h 表滞后 ≤2 小时）。查询分派：跨度 > 24h 走 rollup，否则原始表。

### 路由总表

| 路由 | 任务 |
|---|---|
| `POST /v1/logs|traces|metrics`（HTTP protobuf/JSON + gRPC 同路径） | T111/T112 |
| `POST /_bulk`、`POST /{index}/_bulk` | T120 |
| `POST /rum/v1/beacon`、`GET /rum/v1/config`、`GET /rum/probe.js`、`GET /rum/v1/replay/{session}` | T610/T614 |
| `GET|POST /api/rum-projects` | T611 |
| `GET /api/overview/status` | T314 |
| `GET /api/v1/query`、`GET /api/v1/query_range` | T711 |
| `POST /api/{domain}/search`（DSL） | T310/T311 |

---

## W0 地基

### T000 仓库地基与依赖封板 [S] · 无依赖
- 文件：git init + `.gitignore` 核对（obj/bin/node_modules/data）+ 首次 commit；`Directory.Packages.props` 追加版本条目（仅声明，不引用）：Grpc.AspNetCore、Google.Protobuf、Grpc.Tools、MQTTnet（5.x）、AWSSDK.S3（备用，若走 httpfs 可不引）；`configs/system.example.json` + `system.yaml` 追加 C4 全部配置键模板
- 内容：基线验证 `dotnet build SData.slnx` + 三个测试项目全绿并记录到 STATE.md 构建健康区
- 验收：基线绿证据写入 STATE.md；所有计划包版本已封板

### T010 契约冻结评审 [S] · 依赖 T000
- 文件：仅 `docs/plan/TASKS.md`（如需微调契约）
- 内容：过一遍 C1–C6 与路由表，确认与现有 `IngestService`/`TimeTables`/`Stores` 签名兼容；不兼容处在契约区修正并登记决策日志
- 验收：STATE.md 决策日志登记「契约冻结于 commit xxxx」；W1 解锁

## W1-A 组：M1 OTLP 完整兼容

### T110 proto 类型与 gRPC 依赖 [M] · 依赖 T010
- 文件：`src/ingest/SData.Ingest.Core/SData.Ingest.Core.csproj`（引用包 + 可选 Grpc.Tools）；新增 `Core/Otlp/Generated/`（若走 protoc）或确认现成 NuGet（评估 OpenTelemetry proto 的 C# 包，记录选型到 STATE.md 决策日志）
- 内容：拿到 `ExportTraceServiceRequest/ExportLogsServiceRequest/ExportMetricsServiceRequest` 强类型 + gRPC 服务基类
- 验收：类型可用（一个冒烟单例测试反序列化官方样例报文）；未动任何热文件

### T111 OTLP/HTTP 接收器标准化 [M] · 依赖 T110
- 文件：增 `Ingest.Api/Controllers/OtlpV1Controller.cs`；增 `Core/Services/OtlpProtoParser*.cs`（proto→领域记录，与现有 JSON parser 并存）；改 `IngestController.cs`（旧 `/api/ingest/otlp/v1/*` 保留转发）；改 `tests/SData.Ingest.Tests/`（新增 proto 用例）；`docs/ingest/bdd.md`
- 内容：标准路径 `/v1/logs|traces|metrics`；Content-Type `application/x-protobuf` 与 JSON 双格式；`Content-Encoding: gzip` 解压；鉴权沿用 X-Api-Key 过滤器；全部进 `IIngestService`
- 验收：构造 proto + gzip 报文的单测入库成功；旧 JSON 路由回归绿

### T112 OTLP gRPC 服务 + 通用附加监听器 [L] · 依赖 T111 · **W1 热文件拥有者（Modules/*）**
- 文件：增 `Ingest.Api/Grpc/OtlpGrpcLogs|Traces|MetricsService.cs`；改 `src/SData/Modules/ConfigureMVCModule.cs`（或新增 ConfigureGrpcModule）实现 Kestrel Http1AndHttp2 + C4 的 `SData:Listeners` 通用附加端口机制（http/grpc 二选一协议标记）；改 `MainModule.cs`（模块注册一行）；`docs/ingest/bdd.md`
- 内容：三个 OTLP Collector gRPC 服务实现 → IIngestService；4317/4318 别名端口纯配置实现；gRPC gzip 自动支持
- 验收：Grpc.AspNetCore 客户端（测试内嵌）推送 logs/traces/metrics 入库；`SData:Listeners` 配置 4317 生效

### T113 M1 验收收尾 [S] · 依赖 T112
- 文件：改 `Ingest.Api/Filters/IngestApiKeyFilter.cs`（扩展 `Authorization: Bearer`，保留 X-Api-Key）；`docs/ingest/{bdd,tdd,sdd}.md` 更新
- 内容：Bearer 鉴权；官方 OTel Collector 对接清单（configs/otel-collector-example.yaml 示例）写入 docs；外部环境无 Collector 时按协议标记「e2e 待集成窗」
- 验收：Basic/Bearer 单测绿；docs 四件套齐

## W1-B 组：M2 ES Bulk 接入

### T120 `/_bulk` 端点 [M] · 依赖 T010
- 文件：增 `Ingest.Api/Controllers/EsBulkController.cs`、`Core/Services/EsBulkParser.cs`、`Core/Services/EsBulkResponse.cs`；`tests/SData.Ingest.Tests/EsBulkParserTests.cs`；`docs/ingest/bdd.md`
- 内容：NDJSON（application/x-ndjson）解析，支持 `index/create` 动作（`_index` 取索引名→logs.source），`update/delete` 返回 ES 形状错误项；响应 ES 形状 `{took, errors, items[{status}]}`；缓冲满整批 429（Filebeat 自动重试）；鉴权兼容 Basic（password=Key）；9200 别名 = T112 的 Listeners 配置（本任务只写配置示例，不改 Modules）
- 验收：NDJSON 样例单测（含坏行→errors:true 单项失败语义）；429 语义测试

### T121 logs.source 列 [S] · 依赖 T120（可与 T120 同会话顺序做）
- 文件：改 `Store.Core/TimeTables.cs`（logs DDL 增 source 列）+ 内嵌 DDL 版本；改 `Store.Core/DuckDbSchemaBootstrapper.cs` 或 TimeTables（启动时对近期天表 `ALTER TABLE ADD COLUMN IF NOT EXISTS source`）；改 `Store.Core/LogStore.cs`（查询支持 source 过滤）；`tests/SData.Store.Tests/`；`docs/storage-layout.md`
- 验收：新/旧天表均含 source；过滤查询测试绿

### T122 M2 验收 [S] · 依赖 T121
- 文件：`configs/filebeat-example.yml`；`docs/ingest/{bdd,tdd}.md`
- 内容：Filebeat 直推清单写入 docs；无环境标「e2e 待集成窗」
- 验收：docs 齐；构建绿

## W1-C 组：M4 MQTT 接入

### T140 内嵌 MQTT broker [L] · 依赖 T010
- 文件：增 `src/iot/SData.Iot.Core/Mqtt/MqttBrokerService.cs`（BackgroundService）、`MqttTopicProtocol.cs`、`MqttDeviceAuth.cs`；改 `Iot.Core` csproj（引用 T000 已封板的 MQTTnet）；`configs/system.yaml` 无需改（键已在 T000 模板）；`docs/iot/bdd.md`
- 内容：MQTTnet 服务端（1883，QoS 0/1，retain 支持）；username=DeviceKey / password=SecretHash 校验；ACL：仅允许发布 `iot/{自身 deviceKey}/...`；主题解析（单值与批量 JSON 两形态）→ `IIngestBuffer.TryEnqueueTelemetry`；连接/订阅限本机规模（无集群）
- 验收：进程内 MQTT 客户端测试（MQTTnet client）按主题约定推遥测入库；错误凭证被拒；越权主题被拒

### T141 设备凭证与 LastSeen 批量回写 [S] · 依赖 T010（与 T140 可并行，实体先于 broker 联调）
- 文件：改 `Database.Shared/Entities/DeviceEntity.cs`（+SecretHash）；`DatabaseContext.cs` 不动（无新表）；增 `Iot.Core/Services/DeviceCredentialService.cs`、`DeviceLastSeenFlusher.cs`（30s 批量 UPDATE）；`tests/SData.Database.Tests/`；`docs/iot/bdd.md`
- 内容：设备密钥哈希存储与校验；LastSeen 内存累积批量回写（禁止逐消息写 SQLite）
- 验收：凭证校验测试；LastSeen 批量测试

### T142 M4 验收 [S] · 依赖 T140+T141
- 文件：`configs/mqtt-example.md`；`docs/iot/{bdd,tdd,sdd,sop}` 补齐
- 验收：MQTTX 清单入 docs；无环境标「e2e 待集成窗」

## W1-D 组：M6 RUM 后端骨架

### T610 RUM 模块骨架 + Beacon 摄取 [L] · 依赖 T010 · **W1 的 sln/MainModule 拥有者**
- 文件：增三项目 `src/rum/SData.Rum.{Shared,Core,Api}`（{Domain}SharedModule/CoreModule/ApiModule 脚手架，按根 AGENTS.md 第 6 节）；改 `SData.slnx`（追加 3 行）+ `MainModule.cs`（追加模块）；增 `Rum.Core/Services/RumBeaconService.cs`、`Rum.Api/Controllers/RumBeaconController.cs`；TimeTables/内嵌 DDL 增 `rum_session_/rum_event_/rum_replay_` 表族（本任务先占 DDL，T121 风格的启动修补沿用）；`docs/rum/` 新四件套骨架
- 内容：C3 协议接收（gzip JSON、ProjectKey 校验、204/401/429）；事件缓冲→IngestBuffer 新增 Rum 队列类型（`IIngestBuffer` 扩展方法，Store.Shared 契约增量，需在 STATE.md 决策日志登记）；`/rum/v1/config` 采样下发（项目未建时返回默认全采样）；`/rum/probe.js` 占位静态文件
- 验收：beacon 单测入库（rum_event_yyyymmdd 有行）；401/429 语义测试

### T611 RUM 项目管理 [M] · 依赖 T610
- 文件：增 `Database.Shared/Entities/RumProjectEntity.cs` + DatabaseContext 一行 DbSet；增 `Rum.Core/Handlers/*`（CRUD）、`Rum.Api/Controllers/RumProjectsController.cs`（仅 admin 可写，Controller 角色控制）；`tests/SData.Database.Tests/`；ui 占位页暂不做（G 组）
- 验收：CRUD xunit；只读用户写接口 403 语义测试

## W2-E 组：M3 查询 DSL + rollup + 大屏

### T310 统一查询 DSL 编译器 [L] · 依赖 T010 · **W2 的 Store.Shared 契约落点**
- 文件：增 `Store.Shared/QueryDsl/`（模型 + DataDomain 枚举复用 TimeTables 前缀）、`Store.Core/QueryDsl/QueryDslCompiler.cs` + 白名单校验 + 单元测试 `tests/SData.Store.Tests/QueryDslTests.cs`；`docs/store/bdd.md`
- 内容：C1 全量语义（filters/groupBy/aggregations/bucket/sort/limit）编译为参数化 SQL；表集合来自 TimeTables（本任务直接用，T510 换 ITableResolver）；恶意字段名/op 白名单外拒绝（BusinessException 中文消息）
- 验收：每域至少 6 个编译用例（含时间分桶、json_extract_string 标签过滤、IN 参数化）绿

### T311 各域查询 API 迁移 DSL [M] · 依赖 T310
- 文件：改 `logs/traces/metrics/iot` 四个 Core 的查询 Handler（内部改走 DSL 编译器，请求/响应模型不变——增加可选新字段向后兼容）；各域 Shared 增 `Search*Command`（IModelValidator 齐备）；相应 Controller 增 `POST /api/{domain}/search`；`docs/{logs,traces,metrics,iot}/bdd.md`
- 验收：旧查询 API 回归绿 + 新 search 端点测试绿；`MaxQueryDays` 约束保留

### T312 metrics rollup [M] · 依赖 T311
- 文件：改 `Store.Core/TimeTables.cs`（C6 DDL）；增 `Store.Core/RollupService.cs`（BackgroundService）；改 `Store.Core/MetricStore.cs`（长区间走 rollup）；`tests/SData.Store.Tests/RollupTests.cs`；`docs/storage-layout.md`
- 验收：回填幂等测试；30 天查询走 rollup 的行为测试

### T313 大屏前端 + screens 语义 [M] · 依赖 T010（可先行，不依赖 T310——面板数据接口不变）
- 文件：改 `dashboard` 域（kind=screen 的刷新/轮播配置字段进 ConfigJson）；ui 增 `pages/screens/`（全屏/自动刷新/轮播/KPI 行，按 ui-pages.md）；改热文件 `ui/src/router/index.tsx`、`AppSider.tsx`、`i18n/*`（append-only）；`ui/src/pages/screens/__tests__/`；`docs/dashboard/bdd.md`
- 验收：npm test + build 绿；只读身份隐藏编辑按钮（ui/AGENTS.md 第 11 条）

## W2-G 组：M6 RUM 前端与查询面

### T612 探针 SDK 阶段 A [L] · 依赖 T610
- 文件：增 `ui/packages/rum-sdk/`（独立 TS 包：web-vitals + JS 错误 + PV/SPA 路由 + fetch/XHR 计时 + sendBeacon 上报 + 采样配置拉取）；构建产物托管约定写入 `docs/rum/sop.md`
- 验收：vitest 单测（事件构造/批量/重试）；demo 页接入手册

### T613 RUM 查询面 API [M] · 依赖 T611
- 文件：增 `Rum.Core/Handlers/*`（性能汇总/会话列表/JS 错误聚合）、`Rum.Api/Controllers/RumQueryController.cs`；`docs/rum/bdd.md`
- 内容：Web Vitals p75、PV/UV、错误 Top、会话列表——基于 rum_* 天表聚合
- 验收：聚合查询 xunit 绿

### T614 会话回放 [L] · 依赖 T612+T613
- 文件：SDK 增 rrweb 录制与分段上报（C3 replay-chunk）；Rum 域增回放存储/`GET /rum/v1/replay/{session}`；ui 增 `pages/rum/`（性能/会话/回放三个视图，rrweb-player）；热文件路由/菜单/i18n append-only；`docs/rum/` 更新
- 验收：录制→上报→回放数据链路测试；npm test 绿

### T615 热力与站点地图 [M] · 依赖 T614
- 文件：SDK 增点击坐标采集；Rum 域增栅格聚合查询与页面树聚合；ui 增热力/站点地图视图
- 验收：聚合测试 + 前端冒烟

## W2-I 组：总览聚合

### T314 总览页后端聚合 [S] · 依赖 T010（通道信息随各任务完成自动可见，可最后做）
- 文件：增 `monitor` 或 `store` 域 `GET /api/overview/status`（通道健康/今日量/存储用量聚合）；ui `pages/overview` 扩充（接入健康表 + 存储用量，按 ui-pages.md）
- 验收：聚合测试 + 前端冒烟

## W3-F 组：M5 S3 冷存储

### T511 httpfs 验证与 S3 连接初始化 [S] · 依赖 T311 · **W3 的 DuckDbConnectionManager 拥有者**
- 文件：改 `Store.Core/DuckDbConnectionManager.cs`（启动时按 C4 配置创建 S3 SECRET，未配置则完全跳过）；`tests/SData.Store.Tests/`；验证结论写 STATE.md 决策日志（DuckDB.NET.Full 是否内置 httpfs，缺失则补安装策略）
- 验收：连接初始化单测（mock 配置）；未配置时零行为变化

### T510 ITableResolver 接入查询层 [M] · 依赖 T511
- 文件：增 `Store.Core/TableResolver.cs`；改 `Store.Core/QueryDsl/QueryDslCompiler.cs`（表集合改由 ITableResolver 提供，UNION read_parquet 分支）；`tests/SData.Store.Tests/`；`docs/storage-layout.md`
- 验收：本地+冷层混合区间查询测试（本地 parquet 模拟 S3 路径即可）

### T512 ArchiveService 与三段保留 [L] · 依赖 T510
- 文件：增 `Store.Core/ArchiveService.cs`（COPY TO parquet→行数校验→台账→DROP+CHECKPOINT，幂等）；增 `Database.Shared/Entities/ColdArchiveEntity.cs` + DbSet 一行；改 `Store.Core/RetentionService.cs`（hot/cold/expiry 三段，C4 键）；`tests/SData.Store.Tests/ArchiveTests.cs`、`tests/SData.Database.Tests/`；`docs/store/{bdd,sop}.md`
- 验收：归档幂等/失败重试/台账一致测试；MinIO 环境无则标「e2e 待集成窗」

### T513 M5 验收 [S] · 依赖 T512
- 文件：`configs/minio-compose.yml`（示例）；settings 页冷存储信息卡片（ui 小改）；`docs/store/` 收尾
- 验收：归档后查询结果一致（集成窗补验也可）

## W3-F 组增强：全文检索加速与冷层闭环（T514–T518，2026-11-16 主会话实施，见 STATE 账本）

### T514 logs 行级 id 列 [S] · 依赖 T512
- 文件：`TimeTables.cs`（logs DDL 列首 `id BIGINT`）、`DuckDbSchemaBootstrapper.cs`（存量表幂等补列）、`Store.Shared/Models/Records.cs`（LogRecord.Id）、`IngestWriterService.cs`（落库统一雪花赋值，IIdProvider 注入）、QueryDsl Schemas/Projections（id 数值列，long 绑定防 double 精度丢失）；测试 10 处 writer 构造点同步
- 验收：写入赋 id、旧表迁移补列（8→10 列）、QueryDsl id 过滤；Store.Tests 全绿

### T515 台账驱动冷查询 + 本地冷查 + ColdCache [L] · 依赖 T510/T512
- 文件：增 `ObjectStores.cs`（IObjectStore + Local/S3/Composite，AWSSDK.S3 引用启用）、增 `ColdCacheService.cs`（LRU + 后台预热队列 + 容量淘汰，`data/cache/cold/`）、`TableResolver.cs` 重构（台账逐日/逐分片路径；台账空时 S3 域级通配 / 本地 `data/cold` 目录通配兜底；ColdFiles 与 Glob 互斥）、`ResolvedTables.ColdFiles`、QueryDsl/PromQL 编译器 `read_parquet([...], union_by_name=true)` 分支、`DuckDb.ColdMaxQueryDays`（默认 366，含冷层放宽跨度）
- 验收：本地冷存可查（修复 T512 注释声称但缺失的路径）、台账精确路径替代全桶 LIST、缓存命中/LRU 淘汰/Invalidate、混合热冷查询

### T516 词项索引 sidecar [L] · 依赖 T515
- 文件：增 `ColdTermIndex.cs`（归档 COPY 构建 token+CJK 二元组存在性索引；候选分片查询参数绑定）、ArchiveService 归档时建索引（本地镜像 `data/cold-idx/{domain}/{shard}.idx.parquet` + S3 备份，落台账前构建保幂等）、RetentionService 过期时删对象+索引+镜像、TableResolver 关键词候选分片裁剪（无镜像分片保守保留）、QueryDslExecutor 提取 body match 关键词传入
- 验收：归档含中文/英文正文 → 索引含 bigram/token；关键词命中保留候选分片、未命中裁掉有镜像分片；无关键词不裁剪

### T517 归档分片粒度配置 [M] · 依赖 T516
- 文件：`S3Options.ArchiveGranularity`（day 默认/hour，未知回退 day，设置页热生效）、ArchiveService 小时分片（24 对象 `logs/{yyyyMMddHH}.parquet` + 逐分片索引，空分片跳过，台账先清 (domain,day) 后插多行）
- 验收：hour 模式 3 行跨 2 小时 → 2 对象 + 2 索引镜像 + 分片内排序；day 模式零行为变化

### T518 S3 对象过期删除闭环 [S] · 依赖 T515
- 文件：RetentionService S3 分支（原告警 TODO）→ `IObjectStore.DeleteAsync`（S3 协议删除幂等）+ 失败保留台账下轮重试
- 验收：本地全流程已有 ARCH2 覆盖；S3 分支 @manual（MinIO e2e 集成窗，configs/minio-compose.yml）

### R19 统一查询管线重构 [L] · 依赖 T514–T518（2026-11-16 主会话实施，见 STATE 账本）
- 乱点收敛：五条查询路径仅 QueryDsl 有完整冷层、UI 主通道 analytics/search 无冷层、Resolve 每查一次 SQLite、词项裁剪全域 glob、Promql range 每桶 Resolve、sync-over-async
- 文件：增 `ColdLedgerCache.cs`（台账内存镜像，TTL 5min + Archive/Retention 写后刷新）；`ITableResolver` → `ResolveAsync`（异步全链路）；`TableResolver` 读镜像 + 词项裁剪改**显式 idx 文件列表**（`CandidateShardsSql`）；`AnalyticsExecutor.ExecuteSearchAsync` 经 TableResolver 接入冷层（含冷层放宽跨度；裸 `ExecuteAsync` 不注入）；`LogStore` 并入管线（复用 `QueryDslCompiler.BuildBranchUnion` 唯一冷层拼装，search 关键词走词项裁剪）；`PromqlExecutor` 区间单次 Resolve + 补跨度校验；`SearchSqlExpander` 表提供者升级为 ResolvedTables（本地+冷层分支）
- 范围控制（follow-up 登记不动）：TraceStore/MetricStore/TelemetryStore GET 点查路径不接冷层（trace_id zonemap + rollup 全局表已兜底）
- 性能预算：表解析+裁剪固定开销 < 5ms（内存镜像 + 显式列表）；热窗口搜索 p99 < 50ms；Promql range 1 次 Resolve
- 验收：Store.Tests 104/104（新增 QueryDsl 冷层端到端 + analytics/search 冷层端到端）+ Metrics.Tests 46/46（PromqlExecutorTests 桩解析器修复）

### T520 follow-up（待开卡）：Trace/Metric/Iot GET 点查路径接冷层；设置 UI 补 ArchiveGranularity/ColdCache 绑定；MinIO e2e（ARCH6/COLD-E2E）

## W3-H 组：M7 PromQL 子集

### T710 PromQL 解析选型 [M] · 依赖 T312
- 文件：评估现成 .NET PromQL 解析包（仅取 AST）或 ANTLR grammar 生成；选型结论 + 对比记录写 STATE.md 决策日志；若需新增 NuGet（热文件例外，唯一允许改 Directory.Packages.props 的后续任务）；增 `src/metrics/SData.Metrics.Core/Promql/`（Lexer/Parser/AST + 子集单测）
- 子集：metric name、label 匹配（= != =~ !~）、rate/sum/avg/min/max/count/topk/bottomk/by/without/offset、范围向量 [5m]；不做子查询、histogram_quantile（二期）
- 验收：≥20 个表达式解析用例（含非法表达式拒绝）

### T711 AST→SQL 翻译器 + Prometheus HTTP API [L] · 依赖 T710
- 文件：增 `Promql/PromqlSqlTranslator.cs`（生成走 QueryDsl/MetricStore 同层参数化 SQL，复用 rollup 分派）；增 `Metrics.Api/Controllers/PromController.cs`（`GET /api/v1/query`、`/api/v1/query_range`，Prometheus JSON 形状）；`docs/metrics/bdd.md`
- 验收：rate(http_server_requests_total[5m]) 等端到端查询测试；错误表达式返回 Prometheus 风格 error

### T712 M7 验收 [S] · 依赖 T711
- 文件：`docs/metrics/` 收尾 + Grafana 数据源对接清单（configs/grafana-note.md）
- 验收：Grafana 出图（无环境标「e2e 待集成窗」）

## 末：集成收尾

### T900 全量集成验证 [M] · 依赖全部 done（blocked 任务豁免并登记）
- 文件：五路 e2e 补验（Collector/Filebeat/MQTTX/MinIO/Grafana）证据进各模块 tdd.md；`I18nScanner` 全量跑；docs/README 导航核对；补齐 traces/metrics/iot/monitor 四件套
- 验收：`dotnet build`+全测试+`ui npm test`+`npm run build` 全绿证据链写入 STATE.md

## 前端：去 antd 化改造（2026-11-17 完成）

### T950 design-system v2 替换 antd [L] · 无后端依赖（main-20261117 实施，见 STATE 账本）
- 内容：自研轻量组件层（Tailwind CSS 4 + Radix 原语 + react-hook-form + lucide-react，组件代码全部归仓库）替换 antd 5；12 个页面域全量迁移（layout/login/search/overview/charts/metrics/iot/analytics/dashboards/screens/rum/log-consumption/settings-members）；亮/暗双主题（默认暗，CSS 变量 + data-theme 同步桥，旧 8 套预设 localStorage 值自动归一）；`api/` 契约层、lucene.ts/buildChartSql.ts 纯逻辑、SignalR/rrweb 逻辑零改动；卸载 antd/@ant-design/icons/v5-patch 三依赖
- 关键文件：ui/src/design-system/**（components/ 19 组件 + theme/ + Chart.tsx）、ui/src/index.css（CSS 变量组 + @theme inline + .sdata-control 组件层）、providers/AppProviders.tsx、store/app.ts、layouts/**、ui/AGENTS.md（重写）、docs/ui-pages.md（前端基线节）
- 验收：ui typecheck ✓ + eslint ✓ + vitest **82/82** ✓（14 文件；iot/analytics 各 8 例迁移时补建）+ `vite build` ✓（bundle 3317.73KB→2290.00KB，-31%；gzip 1068.96→742.97KB；余量为 echarts 全量引入，按需摇树列 follow-up）；宿主 5300 真实登录 12 页浏览器走查（渲染健康 + 暗亮×中英四象限切换 + 持久化）；后端零改动
- 遗留 follow-up：echarts 按需引入摇树；Radix Select 无搜索模式（原 antd showSearch 场景以数量可控下拉接受，需要时补 Combobox）

## 前端：URL 条件同步（2026-11-17）

### T960 统一搜索页查询条件同步到地址栏 [M] · 无后端依赖（ui-url-20261117-1830 实施，见 STATE 账本）
- 内容：/search 全部查询条件（source/语法模式/Lucene q/SQL sql/字段/排序/过滤 chips/降噪/时间 range 预设或 from+to/limit/refresh）单向投影到地址栏——复制地址重新打开自动恢复并重查；非法值安全回落默认（未知 range 预设/越界 limit/refresh/损坏 filters JSON/to≤from）；`savedId` 上下文（/saved-searches/{id}）禁用同步；既有 `traceId` 深链参数透传保留；视图偏好（表格/JSON、事件模式）与瀑布图弹层不入 URL
- 关键文件：增 `ui/src/pages/search/searchUrlState.ts`（parse/serialize 纯函数 + 共享常量 LIMIT_OPTIONS/REFRESH_INTERVAL_OPTIONS/DEFAULT_FIELDS/DEFAULT_ORDER_BY/SQL_TEMPLATES 自 SearchPage 迁入）；改 `ui/src/pages/search/SearchPage.tsx`（useState 惰性初始化接 parse + URL 同步 effect（等值守卫防回环））；测试增 `__tests__/searchUrlState.test.ts`（16 用例）+ `SearchPage.test.tsx` 增 4 用例（带参恢复首查/条件回写/traceId 透传/savedId 不同步）
- 文档：bdd @ANALYTICS-S15/S16、tdd 映射行、sdd 组件+决策、ui-pages.md /search 行与关键交互约定
- 验收：tsc ✓ + eslint ✓ + vitest 148/148 ✓（SearchPage 13/13、searchUrlState 16/16）+ vite build ✓
- 遗留 follow-up：T961 其它页面接入

### T961 其它页面 URL 条件同步（待开卡）· 复用 T960 模式
- 候选范围与条件：/charts（chartType/mode/series/groupBy/granularity/时间）、/metrics（name/label/aggregation/step）、/analytics（sql）、/iot（page/搜索关键字）、/rum（projectKey/tab）、/saved-searches（keyword）、/log-consumption（typeFilter）；/settings ?tab= 深链已有
- 模式：每页一个 parse/serialize 纯函数模块（对齐 searchUrlState.ts 形态）+ 惰性初始化 + replace 同步 effect；非默认省略、非法回落
- 验收：每页 `__tests__` 补 URL 恢复/回写用例；bdd 场景落对应域四件套

## 性能专项（PF 组，2026-11-21 立项）

依据：[store/performance-audit.md](../store/performance-audit.md)（发现 F1–F14、方案 §6、决策 D1–D7）。**全部 todo，未认领**。带「决策」的卡在对应 D 登记前不得开工。代码改动须先在 STATE 认领，只改卡内文件；行为变化同步 bdd / tdd / sop。

### PF01 观测补齐：写入分段、锁等待、维护耗时、GC 与内存 [M] · 依赖 无
- 文件：`src/store/SData.Store.Core/IngestWriterService.cs`、`DuckDbConnectionManager.cs`、`RollupService.cs`、`ArchiveService.cs`、`RetentionService.cs`；`src/monitor/SData.Monitor.Api/MonitorController.cs`（`/metrics` 输出处）、`src/monitor/SData.Monitor.Core/Handlers/MonitorHandlers.cs`；`tests/SData.Store.Tests/`
- 内容：flush 分段耗时（追加 / 提交，检查点可得部分）；写门、查询门、交接锁等待直方图；Rollup / 归档 / 保留每轮耗时与行数；`GC.GetTotalPauseDuration()`、`GC.GetGCMemoryInfo().TotalCommittedBytes`；`duckdb_memory()` 追踪值与 WAL 字节。核对 `monitor/sop.md:19` 所指的"DuckDB 写入耗时"指标是否存在，不存在则补齐。
- 验收：指标单测（计数与耗时非负，锁等待可观测）；`/metrics` 本地冒烟可见；业务行为不变，Store.Tests 全绿。

### PF02 压测场景补齐（仓库外）[M] · 依赖 PF01
- 文件：`F:\demo\sdata-bench\plans\pf02-plan.json`、`ScenarioRunner.cs`、`Probe.cs`（不在本仓库）
- 内容：≥1 小时指标写入稳态 + Rollup；保留 / 归档场景（测试用缩短 HotDays）；MQ 场景（需 broker，缺环境标"待集成窗"）；traces 索引 A/B 所需的构建开关；timeline 增加 PF01 指标、`duckdb_memory`、WAL 字节。
- 验收：计划文件通过 smoke；短时和各专项产出 summary.json 与逐秒 CSV；一小时指标、归档/保留、MQ、trace-index A/B 四项当前未全绿不得标 PF02 done。

### PF03 配置 A/B 实验矩阵（只改配置）[S] · 依赖 PF02
- 文件：结果回填 `docs/store/performance-audit.md`（§5 增补表）；不改代码
- 内容：`threads` 2/4/8；`MaxConcurrentQueries` 1/2/4；`MALLOC_ARENA_MAX=2`；`DOTNET_gcServer=0` 对照；DuckDB 压缩与检查点相关设置（取值以 DuckDB.NET 1.5.6 支持的为准）。每组记录写入上限、分析 QPS、明细 p99、RSS 与 duckdb 追踪内存、OOM 次数。
- 验收：threads/max_queries 9组矩阵与候选复跑已完成，暂提议 4/2；MALLOC_ARENA_MAX、DOTNET_gcServer、压缩与检查点对照留后续实验，并与 `alerts/sop.md:12` 及内存复盘约束核对。

### PF04 Rollup 增量化与聚合降本 [M] · 依赖 无（验收对照用 PF01）
- 文件：`src/store/SData.Store.Core/RollupService.cs`；`src/store/SData.Store.Core/DuckDb/Ddl/`（新增幂等水位 DDL，随 DuckDbSchemaBootstrapper 应用）；`tests/SData.Store.Tests/RollupTests.cs`
- 内容：水位（DuckDB 元表，与 DELETE / INSERT 同事务）+ 迟到窗口（与 C6 滞后约束一致），替代每轮 5 小时全量回算；先按 `(name, labels)` 聚合，再对分组计算 `labels_hash`（去掉逐行 md5）；每轮桶数上限。
- 验收：RollupTests 6/6 保持；新增用例：仅回算新关闭桶与迟到窗口、崩溃后自愈、C6 滞后约束；PF02 场景中每轮耗时较基线下降（数值回填 performance-audit）。

### PF05 独立维护门 [L] · 依赖 PF01、PF04、决策 D1
- 文件：`src/store/SData.Store.Core/DuckDbConnectionManager.cs`（门与准入）、`RollupService.cs`、`ArchiveService.cs`、`RetentionService.cs`、`StorePerformanceMetrics.cs`；`tests/SData.Store.Tests/DuckDbResourceTests.cs`
- 内容：维护门并发 1 承接 Rollup 聚合、归档 COPY / 索引构建、保留 DELETE；写门只保留 DDL、目录切换与最终短事务；查询门不被维护任务占用。三门各自有界，总并发有上限。维护等待已输出直方图并可取消。
- 验收：DuckDbResourceTests 全绿（128 MB 下 20 轮摄取 / 宽查询 / 回填 / CHECKPOINT）；PF02 场景中开启 Rollup 时日志写入 p99 不高于基线 +20%，写门持有 p99 < 50 ms。

### PF06 归档 COPY 与词项索引移出写门 [M] · 依赖 PF05
- 文件：`src/store/SData.Store.Core/ArchiveService.cs`、`ColdTermIndex.cs`、`DuckDbConnectionManager.cs`；`tests/SData.Store.Tests/ArchiveTests.cs`、`ArchiveLifecycleTests.cs`
- 内容：COPY、词项索引构建和归档 CHECKPOINT 改用维护门持有下的克隆只读连接，不获取写门；冻结表不再接收写入；S3 上传期间不占写门；行数校验与台账顺序不变（COPY → 校验 → 索引 → 台账 → DROP）。
- 验收：ARCH1–3 与归档生命周期用例全绿；新增：归档期间并发写入零丢弃，写门持有 p99 < 50 ms（MinIO 部分待集成窗）。

### PF07 保留与归档交接锁收窄 [S] · 依赖 无（对照用 PF01）
- 文件：`src/store/SData.Store.Core/RetentionService.cs`、`ColdLedgerCache.cs`、`TableResolver.cs`；`tests/SData.Store.Tests/ArchiveTests.cs`（三段保留、锁外删除与失败重试）
- 内容：`ApplyRetentionAsync` 的交接锁只包目录切换；S3 删除（`ExpireColdObjectsAsync`）与 rollup DELETE 移出交接锁；CHECKPOINT 保留，但移出交接锁。与 `store/sdd.md` "网络操作不占交接锁"对齐。
- 验收：三段保留用例全绿；交接锁持有 p99 < 100 ms（不含 S3 删除，以 PF01 指标为准）。

### PF08 写入器与入口热路径降本 [L] · 依赖 PF01、PF03
- 文件：`src/store/SData.Store.Core/IngestBuffer.cs`、`IngestWriterService.cs`；`src/ingest/SData.Ingest.Core/Services/IngestService.cs`、`IngestItemMapper.cs`、`IngestJsonSerializer.cs`；`src/ingest/SData.Ingest.Shared/Models/IngestModels.cs`；`src/store/SData.Store.Shared/IIngestBuffer.cs`（契约增量须在 STATE 登记）
- 内容：简单 JSON 一次映射后按批提交；批量接口逐项有界入 Channel、保留 accepted/dropped；共享 JSON 序列化选项缓存，避免重复构造；去掉 `Dictionary<string, object?>` 往返、保留原始 JSON 片段、批列表复用和字节预算仍需续做；`LocalTraceExporter` resource 缓存（不改变策略）。
- 验收：Ingest / Store 测试全绿；PF02 对照：饱和时平均 CPU 下降 ≥ 20%（提案）。

### PF09 MQ 批量确认流水线 [L] · 依赖 PF01、决策 D4
- 文件：`src/ingest/SData.Ingest.Core/Consumers/KafkaInstanceRunner.cs`、`RabbitMqInstanceRunner.cs`、`PulsarInstanceRunner.cs`、`MqConsumerSupport.cs`；`src/store/SData.Store.Core/IngestBuffer.cs`（PersistLogsAsync 批化）、`IngestWriterService.cs`（有等待者时立即刷写）
- 内容：消费循环不再逐条等待回执，按批入缓冲、按批确认 offset，保持 at-least-once；`PersistLogsAsync` 一批一个回执；写入器存在持久化等待者时不再固定等待 200 ms。
- 验收：`tests/SData.Ingest.Tests/MqRuntimeTests.cs`、`MqMessageBlockingTests.cs`、`MqLogDecoderTests.cs` 与 `tests/SData.Store.Tests/MqPersistenceTests.cs` 全绿；新增 offset 语义与失败阻塞用例；100 条/消息 ≥ 1 万行/秒（Kafka 集成窗补验）。

### PF10 多连接并发追加验证（仓库外实验）[S] · 依赖 PF03
- 文件：仓库外实验工程 `F:\demo\pf10-concurrent-append\`（不入本仓库）；结论写入 `docs/store/performance-audit.md`
- 内容：在 DuckDB.NET 1.5.6.1 下验证单连接及 2/4/8 连接并发追加同一天表的吞吐、批提交延迟、CHECKPOINT 干扰与数据核验。
- 验收：已完成；并发连接未提供稳定吞吐收益，结论为维持单写循环，不另开多 worker 实施卡。

### PF11 HTTP 429 与字节预算 [M] · 依赖 决策 D2、D5，PF08
- 文件：`src/ingest/SData.Ingest.Api/IngestController.cs`、`src/ingest/SData.Ingest.Api/Controllers/OtlpV1Controller.cs`、`src/ingest/SData.Ingest.Api/Controllers/EsBulkController.cs`；`src/ingest/SData.Ingest.Core/Services/IngestService.cs`；`src/store/SData.Store.Core/IngestBuffer.cs`、`SData.Store.Shared/IIngestBuffer.cs`；`src/infra/SData.Infra.Configuration.Shared/SystemOptions.cs`（新增键按 C4 登记）；`docs/ingest/bdd.md`、`docs/ingest/sdd.md`
- 内容：JSON 接入余量不足时在解析后按字节预检返回 429（附 Retry-After，与 architecture / ingest 文档一致）；OTLP 读取完整报文后解析前预检，再逐条入队；ES Bulk 读取批次后预检；缓冲增加估算字节预算，默认 256 MiB。
- 验收：429 语义测试；OTLP 满载重试无重复（测试）；1 KB / 4 KB 正文饱和 10 分钟无 OOM（PF02 场景）。

### PF12 OTLP 请求体与属性转换降本 [S] · 依赖 无
- 文件：`src/ingest/SData.Ingest.Api/Controllers/OtlpV1Controller.cs`、`src/ingest/SData.Ingest.Core/Services/OtlpProtoParser.cs`
- 内容：请求体一次读入（去掉多余 ToArray / GetString）；EventsToJson 中的 attributes 直接写入 Utf8JsonWriter，不经 Parse + Clone。
- 验收：`tests/SData.Ingest.Tests/OtlpParserTests.cs`、`OtlpProtoSmokeTests.cs`、`OtlpGrpcIntegrationTests.cs` 全绿；OTLP 压测 CPU 对照。

### PF13 查询通道分层 [M] · 依赖 PF05、决策 D7
- 文件：`src/store/SData.Store.Core/DuckDbConnectionManager.cs`、`StorePerformanceMetrics.cs`、`AnalyticsExecutor.cs`；`src/alerts/SData.Alerts.Core/AlertExecutionGate.cs`；`src/monitor/SData.Monitor.Core/Handlers/QueryOverviewCommandHandler.cs`、`MonitorHandlers.cs`；`docs/alerts/sdd.md`
- 内容：告警使用专用槽，不占用用户查询槽；概览 `CountTablesAsync` 结果缓存 5 秒；告警评估保留现有串行预算与超时语义。
- 验收：PF02 混合负载下明细 p99 在分析并发 2 时 ≤ 100 ms；告警预算语义测试通过；alerts/sdd 同步。

### PF14 trace_id 索引 A/B 与策略 [M] · 依赖 PF02
- 文件：`src/store/SData.Store.Core/TimeTables.cs`、`DuckDbConnectionManager.cs`（FreezeDayAsync 删除索引的逻辑）、`TraceStore.cs`；`tests/SData.Store.Tests/DuckDbResourceTests.cs`；`docs/traces/sdd.md`
- 内容：对照组（有索引 / 无索引）测 100 万行写入、文件大小与点查延迟；当前策略保持热表建索引、冻结快照删除索引。
- 验收：A/B 数据已入 performance-audit；热建冷删回归全绿。

### PF15 指标查询 series 维度化 [L] · 依赖 决策 D6、PF03
- 文件：`src/store/SData.Store.Core/MetricStore.cs`；`src/metrics/SData.Metrics.Core/Promql/PromqlSqlTranslator.cs`；`TimeTables.cs`；`RollupService.cs`；内嵌 DDL
- 内容：labels 去重为 series 维度表，行上保存 series_id；标签过滤下推到维度表；C6 rollup 同步调整（契约变更须登记）。
- 验收：迁移幂等；Metrics.Tests 全绿；指标查询对照数据入文档。

### PF16 自身遥测策略执行 [S] · 依赖 决策 D3
- 文件：`src/SData/Telemetry/LocalTraceExporter.cs`、`LocalMetricExporter.cs`；`src/SData/Modules/ConfigureOpenTelemetryModule.cs`
- 内容：保留固定本机资源与现有采集策略；复用链路 resource JSON，并缓存指标标签维度 JSON。
- 验收：`tests/SData.Ingest.Tests/SelfTelemetryTests.cs` 全绿；小批量压测写入量对照；AGENTS 第 5 节同步（若规则变化）。
