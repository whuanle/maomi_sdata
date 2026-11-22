# SData 压测与性能瓶颈分析报告（2026-11-22）

> **2026-11-22 更新**：四个 Phase 优化已全部实施并回归验证，见文末「七、优化实施结果」。


- 环境：Windows 10.0.26300，20 核 / 68GB 内存，单机部署
- 被测：`src/SData` Release 构建，本机 `http://localhost:5300`（已有历史数据 339MB DuckDB）
- 工具：`tool/StressTest`（本目录控制台程序，参考 `F:\demo\sdata-demo-client` 的数据形状与接入协议）
- 采集：`dotnet-counters`（CSV 全程）、服务自身 Prometheus `/metrics`、`/api/monitor/status`、源码走读
- 原始产物：`tool/StressTest/perf/`（已 gitignore）

## 一、压测结论速览

| # | 指标 | 结果 | 说明 |
| --- | --- | --- | --- |
| 1 | 接入 HTTP 层上限 | 43 万~123 万条/s | 8 并发×1000 条批 → 42.7 万条/s；16 并发×500 条批 → 123 万条/s |
| 2 | **落库稳态吞吐（写路径天花板）** | **~3.3 万条/s** | logs 单域 33.7k/s；四域混合 26k/s；与请求并发/域无关 |
| 3 | 过载时 429 比例 | 92%~97.5% | 缓冲（100k 条）瞬间打满，之后绝大部分请求被拒 |
| 4 | 查询并发 8、混合档位 | 30.8 RPS，p50 219ms | 7 个查询档位轮转 |
| 5 | analytics SQL 端到端 vs SQL 执行 | 275ms vs 61ms | **~75% 时间在等查询锁**（MaxConcurrentQueries=1） |
| 6 | 写入+查询混合 | 查询 RPS 30.8→21.1，p50 219→332ms；写入 33.7k→26k/s | 写读互相拖累 |
| 7 | GC 分配速率 | 平均 1.36 GB/s，**峰值 27.4 GB/s**（2s 窗口） | 过载期大量分配是被 429 拒掉的请求白做的 |
| 8 | monitor 锁竞争 | 平均 75 次/s，峰值 415 次/s | 高频 CAS/Channel 竞争 |
| 9 | 线程池 | 最大 24 线程，队列峰值 7 | 线程池不是瓶颈 |
| 10 | GC 停顿占比 | 低（0.03s/2s 平均） | 问题是分配吞吐与 CPU，不是停顿 |

## 二、压测阶段明细

### Phase A：logs 纯写入（8 并发 × 1000 条/批 × 120s）
- 请求 51212，成功 4041，**429 47171（92%）**，失败 0
- 提供 51.2M 条（20.2 GB），接受 ~4.0M 条，稳态 **33.7k 条/s（~16MB/s）**
- HTTP 延迟 p50 5.4ms / p95 16.4ms（HTTP 层很快，卡在缓冲）

### Phase B：四域混合写入（16 并发 × 500 条/批 × 120s）
- 请求 295458，**429 287488（97.5%）**
- 提供 147.7M 条（41GB），接受 ~4.0M 条，稳态仍 **~33k 条/s**；HTTP p50 2.5ms

### Phase C：纯查询（8 并发 × 7 档位轮转 × 120s）
- RPS 30.8，0 失败，HTTP p50 219ms / p95 423ms
- 分档（HTTP p50 / 服务端 took p50）：

| 档位 | HTTP p50 | 服务端 took p50 | 说明 |
| --- | --- | --- | --- |
| analytics-sql | 274.6ms | **61.0ms** | 差值 213ms = 查询锁排队 |
| logs-agg（groupBy+count） | 208.6ms | 207.0ms | took 含锁等待（DSL 计时起点在锁前） |
| logs-hist（1m 分桶 count） | 264.3ms | 263.0ms | 同上 |
| logs-raw（排序取 100 行） | 222.9ms | 222.0ms | 同上 |
| logs-text（body match） | 227.6ms | 226.0ms（p95 447ms） | LIKE 全扫 6.3M 行中文 body |
| traces-agg（avg/p95 时延） | 232.3ms | 231.0ms | 同上 |
| logs-page（GET 分页） | 211.0ms | 无 took | lock 等待 + count/page 两查 |

- 无负载单发基线：logs-agg **53~71ms**（纯 SQL，threads=2）；analytics count(*) **~38ms**（elapsed 0ms，固定开销 ~35ms）；logs-text 单发 **~500ms**（399ms 纯执行，LIKE '%中文%' 无索引全扫）

### Phase D：写入+查询混合（12 写并发 + 8 查询并发 × 120s）
- 写入：429 占 98%，稳态接受 ~26k 条/s（比纯写低 ~23%）
- 查询：RPS 21.1（-31%），p50 332ms（+51%），p99 2074ms
- 查询锁等待累积 `sdata_store_query_lock_wait_seconds`：**12969 次共 3169.6s（平均 244ms/次）**

## 三、瓶颈定位（证据 → 位置 → 原因）

### 瓶颈 1：写入路径 —— DuckDB 事务提交 + 小批量刷新，天花板 ~3.3 万条/s

位置：`src/SData/Store/Core/IngestWriterService.cs`（单线程 flush 循环）+ 参数默认值 `IngestOptions`（BatchSize=5000，FlushIntervalMs=200，BufferCapacity=100000）

证据（服务自身 Prometheus 指标）：
- `sdata_ingest_commit_seconds`：2732 次，累计 **407.3s → 平均 149ms/次**
- `sdata_ingest_append_seconds`：2732 次，累计 17.0s → 平均 6.2ms/次（Appender 本身很快）
- 每 5000 条一批 → 5000 ÷ (0.149+0.006)s ≈ **3.2 万条/s**，与三个阶段实测 26k~34k/s 吻合

机制：
1. `IngestWriterService.FlushAsync` 每轮最多取 BatchSize=5000 条，`transaction.Commit()` 为同步调用（DuckDB WAL fsync + 触发自动 checkpoint，WAL 阈值默认 16MB，压测期间 WAL 常年 12~13MB）
2. HTTP 接入层把 JSON 解析进内存缓冲（100k 条），缓冲瞬间打满；之后 92%~97% 的请求 429
3. **429 路径仍然完整反序列化**：MVC `[FromBody]` 模型绑定发生在 Controller 的 `CanAcceptBytes` 检查之前，被拒批次白白付出 JSON parse + 对象分配 + 每条记录的 UTF8 字节数估算（`IngestBuffer.EstimateRecord`）。计数器显示分配峰值 **27.4GB/s**、CPU 峰值 15 核 —— 大部分是无效功
4. 缓冲 preflight 只检查**字节预算**（256MB），不检查**条数容量**（100k）；logs 平均 ~480B/条时 100k 条才 ~48MB，远小于 256MB → preflight 几乎不拦截，绝大多数 429 来自 `Dropped>0` 路径（条已逐条 TryWrite 失败后计数）

### 瓶颈 2：查询路径 —— 全局查询锁串行（MaxConcurrentQueries=1）

位置：`src/SData/Store/Core/DuckDbConnectionManager.cs:20,244-245`（`_queryLock = new SemaphoreSlim(MaxConcurrentQueries)`，默认 1）、`DuckDbOptions.MaxConcurrentQueries = 1`（`SystemOptions.cs:313`）

证据：
- `/metrics`：`sdata_store_query_lock_wait_seconds` 12969 次 / **3169.6s**，等待直方图主峰在 0.1~0.5s
- analytics 档位端到端 275ms vs `ElapsedMs`（锁后才计时）61ms → 差值即排队
- 无负载单发 logs-agg 仅 53~71ms；并发 8 时服务端 took p50 升到 207ms

机制：
1. 所有用户查询（DSL search、analytics、监控聚合）共用一个并发 1 的信号量；8 并发客户端互相排队
2. `QueryAsync`/`ExecuteBoundedAsync` 内 `CloneConnection()` 每次新建 DuckDB 连接（共享实例，成本低但叠加）
3. analytics 执行外层包 `Task.Run` + 50ms 周期 Timer + 子查询 LIMIT 包装，单查询固定开销 ~35ms

### 瓶颈 3：查询 SQL 本身的重扫（次要，但决定单查询地板）

- DuckDB `Threads=2`（20 核机器）、`MemoryLimit=512MB`（默认，`DuckDbOptions.cs:303-308`）
- logs 天表无索引（仅 traces 有 trace_id 索引）；`body match` = `LIKE '%中文%'` 全表扫 6.3M 行宽表（body~500B+attributes/resource JSON）→ 单发 ~400ms
- 聚合查询要扫全表宽列；count(*) 等"细列"查询很快（elapsed 0ms）

### 瓶颈 4：写读互扰

- 写 commit 期间持 `_writeLock`，查询排队更久（Phase D 查询 p50 +51%、RPS -31%）；rollup（每 ~30s 一次、1.4s/次）与 archive 也在维护锁/写锁上小量争用（`maintenance_lock_wait` 45 次 5.8s）

## 四、附带发现的功能缺陷

1. **DSL 编译 bug**：`POST /api/logs/search` 带 `aggregations` 但不带 `groupBy`/`bucket` 时，`QueryDslCompiler` 生成 `GROUP BY  LIMIT $limit`（GROUP BY 后为空）→ DuckDB 语法错误 → **HTTP 500**。复现：`{"range":{...},"filters":[],"aggregations":[{"func":"count","alias":"c"}]}`
2. `logs-page`（GET /api/logs）响应 `count` 字段恒为 -1（本次压测观察到），前端分页拿不到总数
3. `SearchDslResponse.Took` 计时从锁获取前开始（含排队），analytics `ElapsedMs` 从锁后开始 —— 两个口径不一致，排查时容易误判

## 五、优化建议（按预期收益排序）

写入侧（目标：把 3.3 万条/s 天花板提高一个数量级）：
1. **放大 BatchSize**（5000 → 5 万~10 万，与 BufferCapacity 匹配）：commit 成本近似固定，批越大摊薄越狠；同时按**字节**而非条数控制批，避免大 body 批过小
2. **调 checkpoint**：DuckDB `SET checkpoint_threshold` 调大（如 64~128MB），或改为维护窗口显式 CHECKPOINT，避免 commit 时反复触发 checkpoint
3. **429 快速拒绝**：在模型绑定前用中间件按 ContentLength + 缓冲水位直接 429（`IIngestBuffer.CanAcceptBytes` 已有），并把**条数容量**纳入 preflight —— 过载时可省掉 ~97% 的无效反序列化 CPU/GC
4. `EstimateRecord` 的 `Encoding.UTF8.GetByteCount`（enqueue 与 release 各算一遍）改为轻量估算或缓存长度

查询侧（目标：并发查询不排队、重扫描提速）：
5. **MaxConcurrentQueries 1 → 4~8**（信号量已是可配置项，`SET` 页即可调），配合内存预算验证
6. **DuckDB threads 2 → 8~16**（或 0=自动）、memory_limit 按机器内存上调（当前 512MB，机器 68GB）
7. logs 全文检索接入词项索引（已有 `ColdTermIndex` 基建可复用）或 DuckDB FTS 扩展，替代 `LIKE '%…%'` 全扫
8. 查询连接池化复用（`CloneConnection` 每查询/每 flush 新建）；analytics 固定开销瘦身（Task.Run/Timer/子查询包装）

治理：
9. 统一 DSL 与 analytics 的耗时口径（都给出「排队」与「执行」两段），并把 per-endpoint 查询时延暴露进 `/metrics`
10. 修复第四节两个功能缺陷

## 六、复现方式

```bash
# 写入（Phase A）
export SDATA_INGESTION_KEY=<ingestion key>
cd tool/StressTest && dotnet run -c Release -- --scenario ingest --domain logs --concurrency 8 --batch 1000 --duration 120
# 查询（Phase C）
dotnet run -c Release -- --scenario query --concurrency 8 --duration 120
# 混合（Phase D）
dotnet run -c Release -- --scenario mixed --domain logs,traces,metrics,iot --concurrency 12 --batch 500 --query-concurrency 8 --duration 120
```

服务自身指标：`GET /metrics`（Prometheus）、`GET /api/monitor/status`（JWT）。

## 七、优化实施结果（2026-11-22，四个 Phase 全部落地）

实施内容（对应 docs/plan/perf-optimization-plan.md）：

| Phase | 改动 | 关键位置 |
| --- | --- | --- |
| 1 配置 | BatchSize 5000→50000；FlushIntervalMs 200→500；checkpoint_threshold 16MB(默认)→128MB；DuckDB threads 2→8；memory_limit 512MB→2GB；MaxConcurrentQueries 1→4 | `SystemOptions.cs`、`DuckDbConnectionManager.InitializeAsync` |
| 2 接入 | **429 前置快速拒绝**（授权过滤器在模型绑定前按水位拦截，新增 `sdata_ingest_fast_rejected_total`）；attributes/resource/labels 改 `JsonElement` 直通（消除字典二跳序列化）；`EstimateRecord` 结果按记录缓存（原先 enqueue/release 各算一遍 UTF8 长度） | `IngestBackpressureFilter.cs`（新增）、`IngestModels.cs`、`IngestJsonSerializer.cs`、`Records.cs`、`IngestBuffer.cs` |
| 3 存储查询 | analytics watchdog 惰性化（预算截止才建周期 Cancel 定时器）；DSL 可选列式响应（`columnar:true` → rowsArrays）；Rollup 迟到窗口 `桶数×步长`→固定 10 分钟（1h 表从每 30s 重算 2~4h 降到 ≤10min 窗口）。**查询连接池已试验后回退**：DuckDB 连接保持打开会残留事务上下文阻塞 CHECKPOINT/归档（在 `DuckDbConnectionManager.CloneConnection` 注释中留档） | `AnalyticsExecutor.cs`、`QueryDslExecutor.cs`、`QueryDslModels.cs`、`RollupService.cs` |
| 4 缓存 | DSL 查询结果短 TTL 缓存装饰器：range 尾端对齐 TTL 时间桶（默认 5s），桶内重复查询命中；行数 >1000 不缓存；TTL=0 直通 | `CachedQueryDslExecutor.cs`（新增）、`StoreCoreModule.cs`、`SystemOptions.cs` |

回归数据（同一压测工具、同参数；注意后期阶段当日数据量更大，查询绝对耗时略受影响）：

| 指标 | 基线 | Phase 1 后 | Phase 2 后 | Phase 3 后 | Phase 4 后 |
| --- | --- | --- | --- | --- | --- |
| 写入稳态（logs 纯写） | 33.7k 条/s | **51.9k 条/s** | 49.6k 条/s | — | — |
| 写入稳态（四域混合） | 26k 条/s | **94.4k 条/s** | 76.9k 条/s | — | — |
| 查询 RPS（8 并发全档位） | 30.8 | 76.6 | — | 66.3* | **171.9** |
| 查询 HTTP p50 | 219ms | 84ms | — | 107ms* | **0.6ms**（命中）/ miss 107ms |
| 混合查询 RPS / p50 | 21.1 / 332ms | 52.1 / 132ms | 38.8* / 182ms* | — | — |
| 查询锁等待均值 | 244ms | **61ms** | — | — | — |
| 过载写期 CPU（user，均值/峰值） | 4.59 / 15.4 核 | — | **2.35 / 5.4 核** | — | — |
| 过载写期分配速率（均值/峰值） | 1.36 / 27.4 GB/s | — | **0.17 / 0.29 GB/s** | — | — |
| 429 路径 HTTP p50 | ~3-5ms | ~3-5ms | **0.5ms** | — | — |

\* Phase 2/3 回归时当日 logs 数据量已增至基线的 3 倍以上（每轮压测向当日表累计灌入约 500 万~900 万行），查询绝对耗时受扫描量影响，不构成回归。

说明：
- 写路径当前分段成本：append（行级 Appender API）≈ commit ≈ 各占一半；继续提升吞吐的下一杠杆是列式批量导入（NDJSON/Arrow + INSERT SELECT），已列为后续可选项
- 缓存命中路径 p50 0.6ms；`analytics`（独立执行器）与 `GET /api/logs`（分页处理器）未走 DSL 缓存，维持原延迟
- 快速拒绝语义：整批未解析未接收（accepted=0），客户端按 Retry-After 整批重试；计入 `sdata_ingest_fast_rejected_total`
- 文档同步：`docs/ingest/bdd.md`（INGEST-PF13）、`docs/store/bdd.md`（STORE-PF04 语义更新、STORE-CACHE1、STORE-S39）
- 未实施（决策记录）：logs 全文检索热层优化**暂缓**（match 已被 threads=8 从 ~400ms 降到 ~57ms；热层索引需 1.2 亿项/天级存储或内存，投入产出比不足，方案备选与分词规则见 `ColdTermIndex.ExtractTerms`：ASCII token + CJK 相邻二元组）；写入列式批量导入**否决**（实验 B 数据）；查询锁进一步收窄（应用层读写本已并行，互扰主要来自 DuckDB 内部 IO/CPU/checkpoint）
- 部署建议（优先级最高的剩余项）：SData 数据目录迁移到低延迟磁盘（NVMe）——实验 C 证实 F: 盘 WAL fsync 是 commit 成本主因（7 倍差距），此项收益超过任何剩余代码优化

## 八、写入路径追加实验（2026-11-22 续）

三个后续实验的结论（基准程序在 `F:\workspace\scratch\duckbench`，未入库）：

| 实验 | 结果 | 决策 |
| --- | --- | --- |
| A：checkpoint_threshold 128MB→1GB | commit 均值 620→305ms，但总吞吐不变（~53k 条/s），时间在 append/commit 间转移 | **保留 1GB**（checkpoint 中断频率更低，对查询干扰更小） |
| B：Appender vs NDJSON 批量导入（50k 行微基准） | 隔离环境：Appender 62.6ms append + 57.4ms commit = **120ms/批**；NDJSON serialize 47ms + insert 45ms + **commit 387ms** = 479ms/批（NDJSON 的 commit 反而慢 7 倍） | **否决 NDJSON 方案**，不实施 |
| C：磁盘变量（同一基准分别放 C: 与 F:） | C: 盘 commit 57ms；**F: 盘 commit 397ms（7 倍慢）**；append 不受盘影响（64ms） | **SData 数据目录所在盘（F:）的 WAL fsync 是 commit 成本主因**。部署建议：数据目录放低延迟盘（NVMe）；这是部署项而非代码项 |

服务安静窗口实测（无供给风暴）：flush = append 559ms + commit 276ms ≈ 835ms/50k 行 → 写天花板 ~6 万条/s，与过载实测一致。服务 append（559ms）较隔离基准（64ms）慢约 9 倍，剩余差异归于 DB 规模（当日表 2000 万+ 行、库文件 GB 级）与运行上下文，进一步收益需分段埋点定位（appender 行循环 / Close 落段），暂不再深入。
