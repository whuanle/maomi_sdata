# SData 分层性能优化策略（2026-11-22）

> **实施状态（2026-11-22）**：Phase 1/2/4 全部落地；Phase 3 落地 watchdog 惰性化、DSL 列式响应、Rollup 固定迟到窗口；**查询连接池试验后回退**（DuckDB 连接保持打开会残留事务上下文阻塞 CHECKPOINT/归档，见 `DuckDbConnectionManager.CloneConnection` 注释）。回归数据见 `tool/StressTest/REPORT.md` 第七节。全部 7 个测试项目 662 用例通过。

> 依据：`tool/StressTest/REPORT.md` 压测报告（写入天花板 ~3.3 万条/s、查询全局串行、429 无效反序列化、GC 分配峰值 27.4GB/s）。
> 本文档按层给出可实施的优化项：**位置 → 改动 → 预期收益 → 风险 → 验证**。
> 实施按 Phase 推进，每个 Phase 结束用压测四阶段（A/B/C/D）回归对比。

## 总览：每一层的瓶颈与对策

| 层 | 实测现状 | 核心对策 |
| --- | --- | --- |
| 接入 HTTP 层 | 43 万~123 万条/s 供给，但 92~97.5% 被 429；被拒请求仍完整反序列化 | **429 前置快速拒绝**（模型绑定前按缓冲水位拦截）；attributes/resource 直通不再二跳转换 |
| 缓冲与批量写入 | 内存缓冲（100k 条）+ Appender 批量已存在；但每轮 ≤5000 条 + 每轮一次 149ms commit | **放大批量（5 万~10 万条/批或 32~64MB/批）+ 降低 commit 频率 + checkpoint 阈值调大** |
| 存储引擎（DuckDB） | threads=2、memory_limit=512MB、WAL 默认 16MB 自动 checkpoint | threads 8~16、memory_limit 2~4GB、`checkpoint_threshold` 64~128MB |
| 锁与并发 | `_queryLock` 并发 1 全局串行（等待均值 244ms/次）；写 commit 持锁 155ms | `MaxConcurrentQueries` 1→4~8；写锁收窄；Rollup 改增量 |
| 查询执行层 | 单查询 50~400ms（宽表全扫）；analytics 固定开销 ~35ms | 连接池化；行物化改列式；analytics 瘦身；全文检索词项索引/FTS |
| 缓存层 | 仅接入 key（30s）、表清单、log columns 有缓存 | 增加查询结果短 TTL 缓存、services 列表缓存 |
| GC/内存 | Server GC + Concurrent + RetainVM + CpuGroup + TieredPGO 已配置；分配峰值 27.4GB/s | GC 配置本身已合理；**收益来自减少分配**（见 1/2/4 层）+ 响应流式写出避免 LOH |
| 线程池 | 实测非瓶颈（峰值 24 线程、队列 ≤7） | 仅提高并发查询后为 analytics 原生调用考虑专用线程；min threads 微调 |

---

## Phase 1：配置级调优（低风险，预期最大单笔收益）

全部是默认值/参数改动，不改代码逻辑。

| # | 项目 | 位置 | 现值 → 建议 | 预期收益 |
| --- | --- | --- | --- | --- |
| 1.1 | Ingest BatchSize | `IngestOptions`（SystemOptions.cs:409） | 5000 → **50000** | commit 成本近似固定，摊薄 10 倍 → 写入天花板 3.3 万 → **20 万+条/s** |
| 1.2 | FlushIntervalMs | SystemOptions.cs:414 | 200 → **500**（低水位聚合更久） | 低流量时批量更大 |
| 1.3 | checkpoint_threshold | DuckDbConnectionManager.InitializeAsync 增加 `SET checkpoint_threshold` | 默认 16MB → **128MB** | 减少写 commit 时触发 checkpoint 的概率（commit 均值 149ms 的主要构成） |
| 1.4 | DuckDB threads | SystemOptions.cs:308 | 2 → **8**（0=自动可选项） | 单查询聚合/扫描提速 2~4 倍 |
| 1.5 | memory_limit | SystemOptions.cs:303 | 512MB → **2GB**（可配置提示按内存预算） | 大聚合少溢出 |
| 1.6 | MaxConcurrentQueries | SystemOptions.cs:313 | 1 → **4** | 查询等待均值 244ms → <50ms；并发 8 客户端 RPS 30 → **100+** |

风险与配套：
- 1.1 放大批量后单批内存峰值 ≈ 批字节（logs 约 25~50MB），缓冲容量 100k 条不变即可承接；写入器失败重试整批的粒度变大 → 失败重试日志需保留 batch.Count
- 1.3 checkpoint 变大后 WAL 峰值内存/恢复时间上升；配合 1.1 观察每日 WAL 曲线
- 1.6 并发查询×4、threads=8 需要内存预算：4×8 查询执行内存可能超出 2GB → memory_limit 与 threads/并发联动验证；溢出走 temp_directory（已可配置）

验证：Phase A（写）吞吐 ≥15 万条/s；Phase C（查询）RPS ≥100、锁等待 p50 <50ms；Phase D 互扰幅度收窄。

## Phase 2：接入层代码优化（消灭无效功）

2.1 **429 前置快速拒绝**（收益最大，压测期 CPU 峰值 15 核大部分是白干）
- 位置：`IngestController`（src/SData/Ingest/Api/IngestController.cs）preflight 在 action 内执行，但 `[FromBody]` 模型绑定发生在 action 之前
- 改法：新增接入路径中间件/AuthorizationFilter，在模型绑定前读取 `ContentLength`，对照 `IIngestBuffer.CanAcceptBytes(bytes)` + **新增条数容量水位**（Channel Reader.Count 或近似计数），超限直接 429 短路返回
- 预期：过载时服务 CPU 从 15 核降到 ~3 核；被拒请求延迟 p50 从 ~3ms 降到 <0.5ms；429 路径零分配

2.2 **attributes/resource 二跳序列化消除**（高并发对象开销）
- 位置：`IngestItemMapper.ToLogRecord` / `IngestService.IngestSpans`（src/SData/Ingest/Core/Services/）
- 现状：MVC 反序列化出 `Dictionary<string,object?>` → `IngestJsonSerializer.Serialize` 再转字符串 → 缓冲 → flush 时不再用（落库直接用 JSON 字符串列）
- 改法：请求模型属性改为保留 `JsonElement`/原始 UTF8 段，落库时一次性转字符串；或 IngestJsonSerializer 输出直接复用反序列化源字节（Utf8JsonWriter 缓存）
- 预期：每条记录少一次字典分配 + 一次序列化；配合 2.1，接入热路径分配下降 50%+

2.3 **EstimateRecord 轻量化**
- 位置：`IngestBuffer.EstimateRecord`（src/SData/Store/Core/IngestBuffer.cs:339）enqueue 与 `MarkProcessed` 各调一遍 `Encoding.UTF8.GetByteCount`（全字段 ×2 次）
- 改法：估算用 `chars × 2`（或 attributesJson 缓存长度）；`LogRecord` 增加只读缓存字段 `BodyBytes` 等，一次计算两处复用
- 预期：接入热路径 CPU 减一项（1.2M 条/s 供给时每条 2 次×N 字段 UTF8 计数可观）

2.4 监控补齐：`/metrics` 增加 ingest 批大小分布、429 拒绝原因分布（byte-budget vs item-capacity）、接入请求字节数直方图

## Phase 3：存储与查询执行层

3.1 **写锁收窄 / 读写并行**（写读互扰：Phase D 查询 p50 +51%）
- 位置：`IngestWriterService.FlushAsync`（src/SData/Store/Core/IngestWriterService.cs:160）`LockWriteAsync` 覆盖 append+commit 全程
- 现状：DuckDB 实例本身支持事务级 MVCC 读；应用层写锁额外把读挡在 commit 外
- 改法：评估查询绕过写锁（保留 DDL/表创建/归档一致性的锁），或在 commit 阶段缩小临界区；需要归档（FreezeDayAsync RENAME）与写入的互斥验证
- 风险：中（涉及 DuckDB 事务语义与归档一致性）→ 单独分支验证
- 预期：Phase D 查询 RPS 从 21 → 接近纯查询 30+；写 commit 期间读不再排队

3.2 **查询连接池化**
- 位置：`DuckDbConnectionManager.CloneConnection`（src/SData/Store/Core/DuckDbConnectionManager.cs:347）每查询/每 flush `new DuckDBConnection + Open`
- **决策（2026-11-22）：试验后回退**。DuckDB 连接对象保持打开会在实例上残留未结束的事务上下文（即使 reader/command 已释放），阻塞 CHECKPOINT 与归档（ArchiveTests 证实）；每次连接新建成本仅 2~5ms，正确性优先。已在 `CloneConnection` 注释留档，后续若引入连接复用需先解决事务上下文释放语义

3.3 **analytics 固定开销瘦身**（~35ms/次）
- 位置：`AnalyticsExecutor.ExecuteBoundedAsync`（src/SData/Store/Core/AnalyticsExecutor.cs:242）
- 改法：50ms 周期 `Timer` 改为取消链（`CancellationTokenSource.CancelAfter` + 查询侧周期检查）；`SELECT * FROM (...) LIMIT` 包装保留（语义需要）；行读取循环 `Normalise` 减少 object 装箱
- 预期：analytics 端到端 -20~30ms

3.4 **DSL 结果物化列式化**（大 limit 查询的对象开销）
- 位置：`QueryDslExecutorService.ExecuteAsync`（src/SData/Store/Core/QueryDsl/QueryDslExecutor.cs:88-97）每行 `Dictionary<string,object?>` + 装箱；`SearchDslResponse.Rows` 每行一个字典
- 改法：内部读取按列数组收集，`SearchDslResponse` 增加列式字段（保持 JSON 兼容：序列化时输出列式或行式由参数决定）；原始行查询 limit 10000 时收益明显（万行 × 10 列字典 → 万行 × 1 数组）
- 预期：raw 大 limit 查询端到端 -30%+

3.5 **logs 全文检索提速**（单发 ~400ms 的 LIKE 全扫）
- 现状：`body match` → `LIKE '%中文%'`，logs 天表无索引（仅 traces 有 trace_id 索引，TimeTables.cs:109）
- **决策（2026-11-22）：暂缓**。threads=8 后 match 实测已降至 ~57ms；热层索引需 1.2 亿项/天级存储（每行 ~240 个 CJK bigram）或受限内存倒排，投入产出比不足。分词规则已备（`ColdTermIndex.ExtractTerms`：ASCII token + CJK 相邻二元组），后续若仪表盘对 body 检索提出更高要求再立项（物化索引表 / DuckDB FTS + bigram 列两案备选）

3.6 **Rollup 改增量**（每 30s 重算 60min+4h 窗口，1.4s/次，抢占写锁与 DuckDB CPU）
- 位置：`RollupService`（src/SData/Store/Core/RollupService.cs:16 TickInterval=30s，窗口 60min/4h）
- 改法：记录水位（每桶已聚合的最大时间戳），每 tick 只补算未完成桶；或 tick 间隔放宽到 60~120s
- 预期：常规负载下 DuckDB 空闲 CPU 与写锁争用显著下降

## Phase 4：缓存与进阶

4.1 **查询结果短 TTL 缓存**（仪表盘轮询场景）
- 改法：DSL/analytics 请求规范化签名（SQL+参数+分钟级取整范围）→ `IMemoryCache` TTL 5~10s；缓冲 flush 成功后主动失效当日签名或依赖短 TTL 容忍
- 预期：仪表盘高频轮询命中率 50%+ 时查询层负载减半；不做长 TTL，避免陈旧数据争议

4.2 **响应流式写出**：原始行 limit 10000 的 JSON 响应 ~5MB 会整段进 LOH；改 Utf8JsonWriter 流式（P2，配合 3.4）

4.3 **SQLite 元数据线**：现状健康（key 有 30s 缓存、EF scoped context）；如出现热路径仍可加 WAL 模式确认与 busy_timeout 调整（P2）

## GC 与线程池专项说明（对照压测数据）

- GC 配置（runtimeconfig.template.json）已是 Server GC + Concurrent + RetainVM + CpuGroup + TieredPGO；.NET 9/10 Server GC 默认带 DATAS 自适应堆，**不建议再加开关**；RetainVM 保留段避免抖动，配合 DATAS 观察压测后堆是否回落（压测中 gcHeapBytes 1.5GB 正常）
- **GC 优化的真正杠杆在分配源**：2.1（429 零反序列化）≈ 消灭峰值 27.4GB/s 的主要来源；2.2/3.4 次之；停顿本身只占 0.03s/2s，不是问题
- 线程池实测健康（峰值 24 线程、队列 ≤7、pendingWorkItems=0）：**不需要 ThreadPool.SetMinThreads 大动作**；仅当 3.1 落地、查询并发提高后，为 analytics 的 `Task.Run`（原生调用占住 worker）考虑专用长运行线程池，避免慢查询挤占 Kestrel worker

## 实施与验证节奏

1. 每个 Phase 一个分支 + 一轮压测四阶段回归，数据对照 REPORT.md 基线表
2. 指标对照口径：写入稳态条/s（accepted/duration）、429 比例、查询 RPS/分档 p50、`sdata_store_query_lock_wait_seconds` 等待均值、`sdata_ingest_commit_seconds` 平均值、dotnet-counters 分配速率峰值
3. 文档同步：Phase 落地后更新对应模块 bdd/sdd/sop（DOC-STANDARD 四件套）
