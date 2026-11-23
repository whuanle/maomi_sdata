# store — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[存储布局](../storage-layout.md) · 证据：`tests/SData.Store.Tests`

## 目标

单进程承载海量观测数据（日志/链路/指标/遥测）的 DuckDB 引擎层：高频写入低延迟落库、按天分表秒级保留、小内存约束下查询可用。

## 组件

- `DuckDbConnectionManager`：单例持有数据库实例；写门串行化 DDL、后台写及完整 Appender 事务（表门→建表→写门，门内不重入）；查询门覆盖通用查询、DSL、分析 SQL/搜索及告警计数的执行和结果读取，默认并发 1（配置钳位 1–32），独立连接不与后台写共享主连接。线程默认 2，显式 0 仍自动；查询门与写门独立，不保证单个重查询不会耗尽内存。
- `TimeTables`：按天表名（`logs_/traces_/metrics_/iot_yyyymmdd`）与幂等建表 SQL（traces 建 trace_id ART 索引）。
- `IngestBuffer`（有界 Channel）+ `IngestWriterService`（低水位最多等待配置的 flush interval / 5000 条聚合，按类型+天分组 Appender 写入）；持久化等待者出现时立即刷写，MQ 每批仅最后一条带回执，批列表跨循环复用；分组时按（类型, UTC 日）缓存表名。
- `LogStore / TraceStore / MetricStore / TelemetryStore`：跨天 UNION ALL 查询（全参数绑定），聚合分桶 `epoch_ms(ts) // step * step`。`LogStore` 的分页经 `LIMIT $limit OFFSET $offset` 下推到 SQL，调用方传入的 `LogQuery.Offset` 决定跳过条数，不在内存 Skip。
- `IStoreInfo.CountTablesAsync`：把多张当日表合并为单条 `UNION ALL` count 查询，供 Monitor 总览批量取今日行数，减少查询门获取与连接克隆次数。
- `SearchSqlExpander` 支持 `{{sessions}}` / `{{rum_events}}` → RUM 会话/事件内部天表，统一冷热解析、时间与命名参数绑定及查询跨度上限；Agent 提供结构化白名单查询，不开放任意 SQL，见 [Agents设计](../agents/sdd.md)。
- `AnalyticsExecutor`：只读执行器（单条 SELECT/WITH、写关键字黑名单、行数上限、DateTime/BigInteger 归一化）。[告警计数](../alerts/sdd.md) 使用单域热数据、有界计数和专属连接原生中断，不触发冷层读取。
- `RetentionService`：每小时在源切换交接锁内仅处理天表目录切换与待过期冷台账的短暂隐藏；锁释放后删除 S3/本地冷对象。删除中 `ColdLedgerCache` 同时隐藏对象路径并保留“台账有条目”事实，避免新查询回退到域级通配；删除前失败恢复可见性并留待重试，主对象删除成功后立即提交台账删除，刷新失败时继续隐藏缺失对象。rollup 清理与 CHECKPOINT 也在交接锁外。
- 维护门：`DuckDbConnectionManager` 提供并发 1 的维护准入，Rollup、归档、保留三类后台任务共享它；维护门与写门、查询门独立，维护等待不占用摄取表门或查询名额。维护等待直方图 `sdata_store_maintenance_lock_wait_seconds` 可观测，三门仍各自有界。
- 归档维护：冻结天表的 `COPY`、词项索引构建和归档后 `CHECKPOINT` 使用克隆只读连接执行，不获取写门；台账、隐藏快照、DROP 与目录切换仍走短写事务和源切换交接锁。
- `RollupService`：30秒周期，首轮按 C6 窗口回填；DuckDB `sdata_rollup_watermarks` 持久化每张 rollup 表的完成水位，后续及重启后只重算最近两个关闭桶以接纳迟到数据。最后桶替换与水位更新在同一事务提交。每桶在查询门下先生成连接私有临时汇总（先按 name/labels/bucket 聚合，再计算 labels_hash），仅 DELETE/INSERT 短事务占写门；计算/取消失败不提前删除旧桶。移除高频删插写放大的两个复合 ART 索引，旧库启动幂等 DROP 索引，不删业务表。
- 消费采样的 `diagnostics` 保存有界阶段摘要，旧日表启动幂等补列；共享写入器故障通过缓冲发布安全分类和恢复时间，不返回原始异常。
- 消费通知冷却与投递摘要属于SQLite元数据（`mq_notification_state`），启动由SchemaReconciler只增补表；不写入观测日志表，不随消费会话重建清零。通知预算及边界见 [Ingest设计](../ingest/sdd.md)。
- 消费统计 `mq_consumption_yyyymmdd`：10 秒累计快照，经统一缓冲/Appender 写入，实例/规则/会话隔离；历史不得逐采样求和。沿用日志热保留天数，但不参与日志归档；查询仅经 Monitor 的管理员入口。
- 资源故障的现场证据、可复现实验及剩余限制见 [内存复盘](memory-root-cause.md)；不是靠加配额或隐藏故障防复发。
- `IStorageMetrics` / `StorageMetricsService`：每 15 秒只读采集 DuckDB 追踪内存、溢出、实际配置、主文件/WAL 与数据/临时目录所在卷容量；不执行 CHECKPOINT。失败保留有状态和成功时间的旧值。
- `S3MetricsSampler`：有效设置驱动客户端，每 10 分钟只读分页列举桶/前缀当前对象，30 秒预算，失败一分钟后重试；配置指纹变化立即隔离旧值。只发布完整总量，不含版本历史、服务端剩余空间或原始 SDK 异常。

- `IDuckDbRuntimeSettings`（Settings.Shared）由连接管理器实现：写门内参数绑定调整实例级内存并读回发布快照，已有/新连接共享；启动在结构引导前应用持久化覆盖。设置协调和失败恢复见 [Settings设计](../settings/sdd.md#duckdb-内存)。

## 关键决策

1. 按天分表而非单表+DELETE：保留=DROP，秒级释放；写入天然冷热分离。
2. 同步接入 TryWrite 满载返回 false 并计数（429 提示），有界 Channel 使用 Wait 模式确保并发判满原子。MQ 采用异步 WriteAsync + 持久化回执，批量事务提交后才确认；失败批次保留重试，进程取消不伪报成功。日志 Appender 按库内实际列序写入，兼容旧表补列；首次以空投影读取并校验列名集合后缓存，补列、冻结和删除/重建失效，不逐批扫描系统目录。
3. 用户 SQL 独立执行器收口，黑名单 + 只读连接 + 行上限（注：字符串字面量含黑名单词会被误拒，属可接受 trade-off，见 sop）。
4. 全文检索 = 原文子串匹配（`body LIKE '%输入%'`，EscapeLike 转义 `%_\`，所见即所搜、中文免分词）；不做 ES 式行级倒排（基准实测 100 万行 7ms / 1000 万行 41ms，热层已近内存带宽极限），向 ES 靠齐走渐进路线：天级词项索引 → 分片级索引 → 查询语法子集 → BM25（超大规模场景指向 OpenObserve 等专业系统，单机内网不做行级倒排）。
5. 日志冷层对象带词项索引 sidecar（token + CJK 二元组存在性索引）：归档时构建、本地镜像常驻（`data/cold-idx/`，365 天仅数百 MB），关键词查询先扫本地索引裁候选分片再拉对象——索引是超集过滤，对象内 LIKE 精确匹配，语义不变。
6. 归档 `ORDER BY 主过滤键, ts` 写 parquet：`service/trace_id/name/device_key` 等值过滤经 zonemap 剪 row group，冷查询省带宽。
7. 冷查询路径 = 台账驱动逐分片精确路径（缓存命中本地 / S3 直读+后台预热 / 本地冷存直读），域级通配仅台账为空时兜底且输出前经 glob() 验存——零命中/验存异常 fail-open 跳过冷层分支（read_parquet 对零命中通配在 prepare 阶段抛 IO Error 会连累本地天表查询整体 500，空桶场景必现）；验存探测按「配置指纹+域」缓存结果并熔断（命中 60s / 零命中 30s / 异常 5min），窗口内不再触碰 S3——探测在查询请求路径上，S3 故障时每个窗口每域最多付一次探测延迟，不逐查询拖慢搜索；`IObjectStore` 抽象统一本地与 S3 删除/下载通道；双写并存分片（本地+S3 两行台账）本地优先去重收录。
8. 归档分片粒度管理员可选（`S3.ArchiveGranularity`：day 默认 / hour）：hour 每个非空小时一个对象（仅日志生成对应正文索引），冷查询只拉命中分片；粒度同时作用于对象与索引（二者必须匹配），台账按冻结批次追加，重启恢复不重复发布。
9. S3 SECRET 统一由 `S3SecretBuilder` 组装（启动 SECRET 与连接测试共用）：ENDPOINT 归一化为裸 host[:port]（DuckDB httpfs 不接受协议头，带 `http(s)://` 会拼出 `http://{bucket}.http//host...` → Could not resolve hostname）、`USE_SSL` 按协议头推导（无前缀按 http）、`URL_STYLE 'path'`（MinIO 等 IP 端点虚主机寻址永远无法解析，与 `S3ObjectStore` 的 `ForcePathStyle=true` 一致）。SECRET 是 DuckDB 连接实例内存态，重启即失：`StoreInitializer.StartAsync` 以 `GetS3()`（SQLite 覆盖 &gt; 文件回退）为真相源 `CREATE OR REPLACE` 重建，并同步合并值回 `SystemOptions.S3`（`S3ObjectStore`/`ColdCacheService` 同源生效）；缺此步骤时查询拼出 `s3://` URL 而无匹配 SECRET，httpfs 走默认 AWS 端点（`in region ''` + NoSuchBucket）。
10. 冷备双写并存（本地磁盘冷备）：分片目的地恒含本地键 `data/cold/{domain}/{day[_HH]}.parquet`（与 S3 桶内 `{prefix}/{domain}/...` 结构同构），配置 S3 时每分片追加 `s3://` 键（`BuildShardPlan` 纯函数）；写入顺序本地先、S3 后，逐目的地 `COPY` + 行数校验，每个目的地各落一行台账（同 ShardName，行数一致）；任一目的地失败整轮失败不落台账（全有或全无，冻结表保留下轮覆盖半写重试）——本地磁盘因此成为归档的硬性目的地（磁盘满会阻塞归档批次）。查询侧按 ShardName 去重、本地优先收录，S3 副本跳过（防重复扫描出重复行），S3-only 历史分片仍走缓存/直读；清理经 `IObjectStore` 按对象键逐行路由介质，双写天然对称。

- 自动归档以 UTC 零点为日期基准，天表日期 ≤ 当天减 HotDays 即到期（HotDays>0 且 ColdDays>0）；一天策略归档昨天但不归档今天。S3 URI 不执行本地目录创建，仍按写对象→回读行数校验→索引→台账→DROP 顺序执行。

- 开发宿主将 `data/**` 的 Content/None 项标记为 `Watch=false`，避免数据库、WAL 和运行日志触发代码热更新；不改变文件发布属性或应用配置的默认行为。

## 已知问题

- 跨表 UNION 的天数上限受 `MaxQueryDays`（默认 31；含冷层时放宽至 `ColdMaxQueryDays`，默认 366）限制。
- 指标名列表扫描最近 7 天冷热数据；更早指标仍可按名称与时间范围查询。
- 存量（升级前）冷对象若无 `id` 列，`union_by_name` 单文件调用下不含 id 投影会报列缺失——升级部署需一次性重写过期前归档对象（预发布期暂无存量）。
- 按表日期门冻结批次（内部 archive_GUID_yyyyMMdd），迟到数据写入新热表。网络 COPY 不占交接锁；发布时等待查询结束、强刷台账并隐藏热副本后清理。新对象位于 domain/batchId/shard.parquet，日志独有正文索引；重启依台账隐藏已发布副本，未发布批次继续查询并重试。
- 性能专项（维护任务持锁范围、写入单线程与入口降本、查询通道分层、内存构成）见 [性能审计与方案](performance-audit.md)：PF04/PF07/PF08/PF09/PF12 已有部分实现；维护门、入口预解析背压、查询通道分层及内存实验仍待后续任务与决策。
