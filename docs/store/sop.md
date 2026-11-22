# store — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md)

## 配置

- 搜索告警的预算、热数据限制和超时处置见 [Alerts 运维](../alerts/sop.md)。不要用返回行数推断扫描成本；按生产近似数据复测搜索 P95 与摄取吞吐。

- `SData:DuckDb`：Path / MemoryLimit（默认 512MB）/ Threads（默认 2，0=自动）/ MaxConcurrentQueries（默认 1，钳位 1–32）/ QueryTimeoutSeconds（默认30秒，1–300）/ MaxQueryDays / Retention.{Logs,Traces,Metrics,Iot}。
- 内存限制可在管理员系统设置的 DuckDB 页签手动调大，保存立即生效、重启恢复；容量格式、资源余量及失败处置见 [Settings运维](../settings/sop.md)。文件 MemoryLimit 仅作未保存时的回退。
- 内存复发排查见[根因复盘](memory-root-cause.md)：启动核对有效内存和线程；资源采样Debug日志含类别，失败时限频Warning记录操作类型与类别。比较结果物化、聚合写放大和摄取积压，不把RSS缓存增长直接认定为泄漏；rollup旧索引启动移除，过期桶随指标总保留期清理。
- Rollup 每 30 秒运行；DuckDB 持久化水位，首轮覆盖 C6 窗口，后续及进程重启后只重算最近两个关闭桶。水位更新与最后桶替换在同一事务内。超过该迟到窗口的数据不会自动修正已生成的旧汇总；不要依赖重启来触发整窗回填，需另行执行受控回补。回填期间查询门仍受占用，只有替换桶的短事务占写门。
- Overview 资源采样：本地 15 秒、S3 完整分页 10 分钟（失败 1 分钟重试，30 秒预算）。S3 需 ListBucket 权限；无列举权限不代表归档不可用。统计只含配置前缀下当前对象，不是服务端磁盘容量。采样不写对象、不执行 CHECKPOINT。
- 消费统计为 `mq_consumption_yyyymmdd`，沿用日志热保留天数并按小时清理，不上传冷层。10 秒采样的累计数须按会话解释，不可跨样本求和；缓冲繁忙会合并采样并记录 sampling_gaps，不能作为消息审计账本。
- 消费统计 diagnostics 列启动时幂等补齐；失败批次保留重试时，在 Status 查看共享写入器的安全原因及失败/恢复时间。不要删除 WAL 或通过确认消息绕过失败。
- 消费通知冷却位于SQLite的 `mq_notification_state`，不要为恢复消费而清除此表，否则会重置通知限流。冷却与投递边界见 [Ingest运维](../ingest/sop.md)。
- Schema 手动管理：`dotnet run --project tool/SchemaSync -- duck`（幂等执行内嵌 DDL）。

## 常见问题

| 现象 | 处置 |
|---|---|
| 开发环境反复提示数据库 WAL 更新但无代码变化 | 更新宿主项目后重启 `dotnet watch`；`data/**` 已排除监听，不要删除 WAL。用 `dotnet watch --project src/SData/SData.csproj --list` 确认不含数据目录 |
| 写入后查询无数据 | 缓冲 ~200ms 才落库；确认 `/metrics` 的 `sdata_ingest_processed_total` 是否增长 |
| 查询报 "Only SELECT/WITH queries are allowed" | 分析通道只允许单条只读查询；SQL 字面量中不要出现写关键字（如注释含 DELETE 也会被拒） |
| `storage_memory` / Out of Memory | 先减少查询并发、线程和扫描范围，检查追踪内存及临时磁盘空间；默认 Threads=2/MaxConcurrentQueries=1。降低引擎内存配额会加重配额不足，不是通用修复；有宿主余量时才评估上调。`memory_limit` 不是进程总内存硬上限 |
| `database_invalidated` / FATAL | 引擎已失效，普通重试不能恢复；保存日志并修正资源预算后协调重启，禁止删库/WAL、强行确认或绕过回执。重启前内存批次不持久，未确认消息依赖 broker 重投，允许重复 |
| 查询大区间超时 | 区间超过 `MaxQueryDays` 会被拒绝；用聚合端点降采样 |
| 磁盘不释放 | 保留策略每小时执行 DROP + CHECKPOINT；过期 S3/本地对象删除、rollup 清理与 CHECKPOINT 均在源切换交接锁外执行。可手动 `CHECKPOINT` |
| 冷对象删除失败 | 日志会保留台账并将对象重新暴露给查询；下一轮幂等重试。主对象已删除但台账提交失败时保持隐藏，禁止手工删除台账绕过重试 |
| 维护任务排队或停滞 | 查看 `sdata_store_maintenance_lock_wait_seconds`；Rollup、归档、保留只共享并发 1 的维护门，不占写门或查询名额。只取消维护任务或等待当前维护轮结束，不删除 WAL |
| 归档 COPY 或词项索引缓慢 | 冻结批次已不占写门；若写入仍受影响，先查维护门等待、源切换租约与磁盘/S3 延迟，保留台账不可手工删除 |
| 冷层查询报 No files found | 现版本已修复（@STORE-COLD4）：台账为空时兜底通配先经 glob() 验存，零命中/端点不可达一律跳过冷层分支，本地天表查询不受空桶影响；若仍复现说明运行的是旧版本，升级后重启即可 |
| S3 连接测试报 Could not resolve hostname 且 URL 含 `bucket.http//` | 端点带了协议头进 ENDPOINT 的历史版本缺陷（已修复）；现版本端点可带/不带 `http(s)://` 前缀（自动剥离归一化），确认升级后再测 |
| 验存/连接测试报 `NoSuchBucket: The specified bucket does not exist` | 两种成因：① Bucket 在端点上不存在（S3 协议不自动建桶）——用 MinIO 控制台（:9001）或 `mc mb` 先建桶，或核对设置页 Bucket 名（示例里 `sdata` 是 AccessKey，示例桶名是 `sdata-cold`）；② 历史版本重启后 DuckDB SECRET 丢失，请求走了默认 AWS 端点（日志特征 `in region ''`）——现版本启动即按设置页持久化值重建 SECRET（@STORE-COLD5），升级重启即可。查询不受影响（fail-open + 探测熔断 @STORE-COLD6，每个窗口每域最多一次验存延迟，其余查询零开销走本地热数据） |
| 配置了 S3 后搜索页变慢 | 历史版本验存探测逐查询同步打 S3（端点故障时一次探测拖住整次查询）；现版本探测结果按窗口缓存（@STORE-COLD6：命中 60s/零命中 30s/异常熔断 5min）。仍慢请先看启动日志有无 `S3 SECRET created`（无则 SECRET 未建，升级重启）；再测端点连通性（MinIO 控制台 :9001 可打开即端点正常） |
| S3 连接/冷读报无法解析 `bucket.192.168.x.x` 类域名 | IP 端点不能走虚主机寻址；本服务 DuckDB SECRET 固定 `URL_STYLE 'path'`（与 AWSSDK 通道 ForcePathStyle 一致），若自改 SECRET 去掉该项即复现 |
| 桶里只有连接测试对象，没有业务数据 | 确认已部署修复版；S3 页本地保留设 1 天并保存，数据保留页核对冷存>0（两页分别编辑本地与冷存天数）。UTC 零点后昨天的天表进入下一轮十分钟扫描；查看 `Archive sweep failed` 与 `Archived` 日志，核对业务目录 Parquet/索引和台账；连接成功不等于正式归档成功。未配置 S3 时仅写本地，既有本地冷对象不会自动迁移 |
| 归档后天表消失、数据变慢 | 该天表已转冷层 parquet（台账 `cold_archive` 可查对象键）；长区间走 `metrics_rollup_1h`（T312），冷层 parquet 经 read_parquet UNION 查询（T510） |

- 分析占位符 `{{sessions}}` / `{{rum_events}}` 统一解析当前范围的 RUM 会话/事件冷热来源；无来源时返回空结果。不要以客户端输入拼接表名。Agent 的目录、结构化查询与截断处置见 [Agents运维](../agents/sop.md)，冷完整性未知不能当作全量结果。

## 验收流程

1. `dotnet test tests/SData.Store.Tests` 全绿；本地 DLL 被占用时加 `-p:OutputPath=bin/MqVerify/`，不要停止他人实例。
   MQ 等待落库回执：writer 出错会保留批次重试，确认前故障由 broker 重投，可能重复；保留策略/队列 TTL 到期仍可能淘汰积压，不能承诺无限离线不丢。
2. 启动服务 → 接入样例数据 → `/api/logs`、`/api/traces/{id}`、`/api/metrics/query`、`/api/analytics/query` 均返回数据。

## 冷存储（MinIO）对接清单（T513）

1. `docker compose -f configs/minio-compose.yml up -d`（建桶：`... run --rm mc`）。
2. 在设置页「S3 冷存储」填写 Endpoint `http://localhost:9000`、Bucket `sdata-cold`、Prefix `sdata/archive`、Access Key `sdata`、Secret Key `sdata12345`，保存即重建 S3 SECRET，无需重启（S3 不再从配置文件读取）。端点带/不带 `http(s)://` 前缀均可（自动归一化为裸 host[:port]）；MinIO 的账号/密码即 Access Key/Secret Key（S3 协议仅此一种认证形式，设置页直接填入即可）。
3. 空桶首配验证（@STORE-COLD4，桶内尚无任何域对象时）：保存 S3 设置 → 连接测试（写 `.sdata-conn-test.parquet` 测试对象，空桶可写即通过）→ 立即对日志/指标/链路各域发起搜索与数据分析，均应正常返回本地热数据（glob 验存零命中跳过冷层，日志见 Debug「兜底通配零命中」；端点不可达时为 Warning「验存失败」，本地查询同样可用）。
4. 在设置页「三段保留」配置各域 HotDays/ColdDays（对应 `SData:Retention:{Domain}`），使存在待归档天表（或手工构造历史天表），等待 ArchiveService 一轮（≤10 分钟）。
5. 归档一致性验证：记下归档前台表查询结果 → 归档后天表已 DROP → 用 `POST /api/{domain}/search`（同 range）复查，结果一致（同 range 需在 MaxQueryDays 内）；台账核对 `SQLite cold_archive` 的对象键与行数。
6. e2e（MinIO/真实 S3）无环境时标「e2e 待集成窗」；S3 超期对象清理同窗补验（本地冷目录清理已由阻塞/故障回归覆盖，网络删除不占源切换锁）。
- 四类归档验收：logs/metrics/traces 与 rum_session/rum_event/rum_replay 分别核对对象、台账、归档前后查询。新批次目录不可手工合并覆盖；失败保留内部 archive 批次，重启自动恢复，禁止直接删除这些表。启动首轮仍等待十分钟，保存不是立即上传。
