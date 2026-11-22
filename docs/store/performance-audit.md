# 写入与查询性能审计与优化方案（2026-11-21）

> 关联：[SDD](sdd.md) · [BDD](bdd.md) · [TDD](tdd.md) · [SOP](sop.md) · [内存复盘](memory-root-cause.md) · 任务卡：[plan/TASKS.md · PF 组](../plan/TASKS.md) · 账本：[plan/STATE.md](../plan/STATE.md) · 证据：压测报告 `F:\demo\sdata-bench\report\SData压测评估报告.md`，逐秒原始数据 `F:\demo\sdata-bench\results\full\scenarios\*\server_timeline.csv`

状态：**部分实施（2026-11-21）**。PF01 已在 `/metrics` 暴露 GC 暂停与提交内存、DuckDB 跟踪内存及 WAL 的后台采样，以及写/查/源/表锁等待、源切换独占锁持有时间、摄取阶段、rollup/归档/保留耗时直方图和成功行/工作项计数器；宿主真实 HTTP 抓取已验收（HTTP 200、`text/plain`，核心指标齐全，直方图桶单调且 `+Inf` 与 `_count` 一致），性能 A/B 仍待验收。PF04 已实现 DuckDB 持久水位、两桶迟到窗口、同事务水位推进、先聚合后哈希及临时表聚合移出写门；定向测试通过，故障注入验证水位更新失败时最后桶回滚，并在重开 DuckDB 管理器后保留旧状态，解除故障后重试成功；进程强杀和压测仍待验收。PF07 已将 S3/本地冷对象删除、rollup 清理与 CHECKPOINT 移出源切换交接锁；删除对象在短交接锁内从候选台账隐藏，删除阻塞时新查询不等待，失败后恢复可见与重试，归档/保留类 17/17。PF05 已建立并发 1 的维护门，并让 Rollup、归档、保留共享它，维护等待可观测且不占写门/查询名额，相关定向 40/40。PF06 已把冻结批次的 COPY、词项索引和归档 CHECKPOINT 改为维护门持有的克隆只读连接，不再占写门；归档与生命周期回归 18/18。PF08 已让简单 JSON 四类按批映射并提交缓冲，共享 JSON 序列化选项缓存；批量满载 accepted/dropped 回归通过，原始 JSON 片段直传、字节预算和 CPU 压测仍待续做。PF09 已实现批量拒绝记录回填、单批持久化回执与等待者立即刷写；MQ 解码/阻塞与持久化回归通过，Kafka/RabbitMQ/Pulsar 消费者端批量确认仍待集成验收。PF11 已统一简单 JSON/OTLP/ES Bulk 超预算 429 + Retry-After，并按原始批次和载荷估算保护缓冲；Ingest 全量 140/140、MQ持久化定向14/14。PF10 已在 DuckDB.NET 1.5.6.1 仓库外实验中验证多连接追加：未获得稳定吞吐收益，结论维持单写循环，不拆多 worker。持锁 p99 与部分压测仍待长稳。PF12 已减少 OTLP HTTP 请求体复制，并让 span event attributes 直接写入 JSON。PF13–15 及 D1–D7 决策项仍待实施或实验；状态以 [plan/STATE.md](../plan/STATE.md) 为准。

## 1. 范围与证据边界

- **范围**：写入路径（HTTP JSON / OTLP / MQ → 缓冲 → DuckDB 按天表）、查询路径（查询门、分析 SQL、明细检索、告警计数）、维护任务（Rollup、归档、保留）、内存构成。不含 CQRS/MediatR 管线、鉴权、SQLite 元数据（非热路径，已排除）。
- **证据来源**：
  1. 压测报告（镜像 4fc2b03，容器 2 GiB，默认配置，2026-11-20 23:00 – 11-21 00:30）；
  2. 代码静态审阅（HEAD 4fc2b03；store 目录无未提交改动）；
  3. 对报告逐秒原始数据的只读复核（§5）。复核脚本未入库，计算方法见 §5。
- **未做**：未重跑压测，未做 dotnet-trace 或 DuckDB profiling。所有"可能原因"在 PF01–PF03 产出数据前不作为结论。

## 2. 与既有设计的关系（先读）

- 查询门→写门的顺序、查询并发默认 1、线程默认 2，是 [内存复盘](memory-root-cause.md) 为控制无界并发与 OOM 风险做的**有意取舍**。本文不推翻这些约束，只讨论它们的**持有时长与覆盖范围**能否收窄。
- [store/sdd.md](sdd.md) 声明"网络 COPY 不占交接锁"。F3 指出保留流程的 S3 删除违反了这一原则。
- [architecture.md](../architecture.md) 第 44 行认为"单写线程 + 写锁对单机够用"。压测的写入上限与此假设吻合，但未经剖析证明，本文列为待验证假设（F4）。

## 3. 压测现象与代码位置（摘要）

| 压测现象（报告） | 对应代码位置 | 状态 |
|---|---|---|
| 日志写入上限约 6 万行/秒，与并发无关；单连接即可达到 | `src/store/SData.Store.Core/IngestWriterService.cs:115-175` | 待剖析（F4） |
| CPU 平均 1.2–2.0 核、峰值 3.2 核；磁盘写 23–32 MB/s | `DuckDbConnectionManager.cs:177-180`（threads=2） | 待实验（F4、PF03） |
| 饱和后 21–59% 的行被丢弃，但请求体仍被完整解析 | `src/ingest/SData.Ingest.Api/IngestController.cs:68-70`，`src/ingest/SData.Ingest.Core/Services/IngestService.cs:56-74` | 已确认（F7） |
| 分析 SQL 约 7 qps，明细约 150 qps，查询串行 | `SystemOptions.cs:313`，`AlertExecutionGate.cs:33`，`AnalyticsExecutor.cs:73` | 已确认（F8） |
| 长稳 15 分钟查询 p99 周期性尖峰（最高 1.7 s） | 未归因（见 §5） | 未证明 |
| RSS 1.5–2.0 GiB；缓冲按条数计量；≥1 KB 正文 OOM | `SystemOptions.cs:399`，`StorageMetricsService.cs:55-67` | 已确认（F9） |
| MQ 路径无报告数据 | `KafkaInstanceRunner.cs:148`，`MqConsumerSupport.cs:88`，`IngestWriterService.cs:55-58` | 代码推断，待 PF02 验证（F5） |

## 4. 发现（按影响排序）

### F1 Rollup 每 30 秒重算最近 5 小时指标，并在写门与查询门内逐桶聚合（P0）

- **机制**：`RollupService.cs:15-17` 每 30 秒（`:41`）对 5m 表回算最近 60 分钟（12 桶），对 1h 表回算最近 4 小时（4 桶），合计每轮扫描约 5 小时的原始指标。每桶先取查询门（`:85`），再取写门（`:103`），在写门内执行 `CREATE TEMP TABLE … GROUP BY`，并对**每一行**计算 `md5(coalesce(labels,''))`（`:93`）。
- **影响**：持有时长与窗口内行数成正比。估算：指标写入 1k 行/秒时，每轮约 1800 万行待聚合（需 PF01 实测）。写门被占用期间，日志与指标的落库全部停止，缓冲堆积后丢弃。
- **与既有设计**：写门持有是内存复盘的有意取舍。**问题在于每轮都回算 5 小时并逐行哈希**，这一点在现有文档中没有说明。
- **方向**：水位推进（只补新关闭的桶）+ 迟到窗口（与 C6 滞后约束一致）；先按 `(name, labels)` 聚合，再对分组计算哈希；每轮桶数上限；维护门化（见 PF05）。

### F2 归档 COPY 与词项索引构建经 `ExecuteWriteAsync` 占用写门（P0）

- **机制**：`ArchiveService.cs:205-207` 的 `COPY (SELECT … ORDER BY …) TO …`，以及 `:228-236` 的词项索引构建，都经由 `ExecuteWriteAsync`，即写门。目标为 S3 时，整段上传期间写门都被占用。
- **影响**：按文件默认保留（日志热 30 天、冷 90 天，`SystemOptions.cs:189`），数据超过 30 天即触发，每天一次；积压时可能连续数小时。
- **与既有设计**：冻结后的表（`DuckDbConnectionManager.cs:121-142`，`FreezeDayAsync`）已改名，且不再接收写入（迟到数据进入新热表）。COPY 只读该表，理论上不需要写门。
- **方向**：在维护门内用只读连接执行；写门只保留目录切换的短事务。

### F3 保留/归档的交接锁覆盖整轮，包括 S3 删除与 CHECKPOINT（P0）

- **机制**：`RetentionService.cs:105` 取得源切换独占锁，直到 `:132` 的 `CHECKPOINT` 结束，期间包括冷对象删除（`ExpireColdObjectsAsync`，`:125`，内含 `_objectStore.DeleteAsync`，约 `161-200` 行）、过期 DROP、rollup DELETE（`:129`，`:142`）。归档在 `ArchiveService.cs:243-249` 持交接锁，之后执行 `CHECKPOINT`（`:249`）。
- **影响**：独占锁要等待所有在途查询结束，且阻塞新查询。保留轮次每小时一次（`RetentionService.cs:84`），每次归档一次。
- **与既有设计**：store/sdd 明确"网络 COPY 不占交接锁"，S3 删除却在锁内。CHECKPOINT 的目的是收缩 WAL（内存复盘也明确不靠删 WAL 来释放内存），这一意图应保留，但 CHECKPOINT 应移出交接锁。
- **观测与边界**：`sdata_store_source_switch_lock_hold_seconds` 记录独占源切换租约从获取到释放的时间。PF07 已通过台账候选隐藏解除网络删除的锁覆盖，删除期间保持“台账非空”事实以禁止域级通配回退；删除失败恢复可见，主对象删除成功后优先提交台账。当前仍需以 PF02/PF03 数据确认持锁 p99 < 100 ms。

### F4 写入路径单线程串行，DuckDB 只开 2 线程（P1，待实验）

- **机制**：`IngestWriterService.cs:115-175` 是单循环：取批 → 分组 → 建 Appender → 追加 → 提交 → 下一批。每批 `CloneConnection()`（`:155`）并持有写门（`:154`）。`threads=2` 是实例级设置（`DuckDbConnectionManager.cs:177-180`），检查点压缩与查询共享这 2 个线程。
- **证据**：报告显示 batch 100–5000 与单连接的吞吐都是约 6 万行/秒。这说明瓶颈在每行成本，而不是每批的固定开销。饱和时 CPU 峰值 3.2 核，与 2 线程的上限接近。
- **未证实**：提交路径（WAL 写入、自动检查点）是否在写入线程上同步执行。需要 PF01 的计时拆分与 PF03 的实验来确认。
- **方向**：先计时拆分（追加 / 提交 / 检查点），再做 threads 对照。多写入 worker 的前提是 PF10 的语义验证。
 - **PF10 结论（已实验）**：DuckDB.NET 1.5.6.1 在 threads=2/4、1/2/4/8 连接下无稳定多连接收益；最优 1连接/threads=2 为 33.99k rows/s，2连接/threads=2 为 31.23k rows/s，8连接同样约 31.76k rows/s，4连接CHECKPOINT约 28.23k rows/s。批提交 p99 随连接数从 44.58ms 增至 704.29ms；按时间与行数核验零异常。结论：保持现有单写循环与写门，不拆多 worker。

### F5 MQ 消费逐条等待落库回执，写入器低水位固定等待 200 ms（P1）

- **机制**：Kafka（`KafkaInstanceRunner.cs:148`）与 RabbitMQ（`RabbitMqInstanceRunner.cs:118`）逐条 `await` 处理；`MqConsumerSupport.cs:88` 等待 `PersistLogsAsync`（`IngestBuffer.cs:161-172`，逐条 TCS + WhenAll）。写入器在水位低于 `BatchSize/2`（2500）时固定 `Task.Delay(FlushIntervalMs)`（`IngestWriterService.cs:55-58`，默认 200 ms）。
- **影响（推导）**：单实例吞吐 ≈ 每消息记录数 ÷ (0.2 s + 刷写耗时)。每消息 100 条时约 450 行/秒，远低于 HTTP 路径。
- **与既有设计**：MQ"解析完整、持久化后确认"是 at-least-once 的必要语义，保留。问题在于**逐条串行等待**与**固定延迟**。

### F6 入口 JSON 解析与逐条转换的 CPU 与分配开销（P1）

- `IngestController.cs:68` 以 `IngestLogItem[]` 绑定；`IngestModels.cs:41, 46` 的 attributes / resource 为 `Dictionary<string, object?>`，反序列化时装箱。
- `IngestItemMapper.cs:26-27, 38-46` 每条记录再把 attributes / resource 序列化为字符串。
- `IngestBuffer.cs:149-158` 每条记录一次通道写。
- `IngestWriterService.cs:60` 每批 `new List<IngestItem>(5000)`，约 120 KB，进入 LOH。
- **状态**：代码推断。占 CPU 的比例需要 PF01 与剖析确认。

### F7 缓冲满时先完整解析再丢弃，返回 200 而非文档约定的 429（P1，契约漂移）

- `IngestController.cs:68-70` 与 `IngestService.cs:56-74` 对 JSON 接口返回 **200** 与 `dropped` 计数。但 [architecture.md](../architecture.md) 第 71 行与 [ingest/sdd.md](../ingest/sdd.md) 第 29 行约定 HTTP 通道缓冲满返回 **429**。
- OTLP（`OtlpV1Controller.cs:116-133`）返回 429，却在返回前已经部分入队。客户端重试会产生重复。
- **影响**：饱和时 21–59% 的行在解析后被丢弃，CPU 与带宽白白消耗。客户端默认把 200 视为成功，丢失不易被发现。
- **方向**：按文档统一为 429（见 D2），并在解析前做余量判断。

### F8 唯一查询槽被告警、Rollup、概览计数与自动刷新共用（P1）

- **槽**：`SystemOptions.cs:313`（`MaxConcurrentQueries=1`）。
- **占用方**：告警（`AlertExecutionGate.cs:33`，预算默认 15 秒/分钟；`AnalyticsExecutor.cs:73`）；Rollup（`RollupService.cs:85`）；概览计数（`QueryOverviewCommandHandler.cs:100` 调用 `CountTablesAsync`，前端 `OverviewPage.tsx:62` 每 10 秒刷新）；仪表盘与搜索的自动刷新（`DashboardDetailPage.tsx:134`，`SearchPage.tsx:545`）。
- **连接**：每次查询新建连接（`DuckDbConnectionManager.cs:230-235`）。
- **影响**：明细查询服务端仅 6–7 ms，却排在 140 ms 的分析查询之后（报告 4.7）。
- **与既有设计**：[alerts/sdd.md](../alerts/sdd.md) 已记录"默认单槽""预算超限延后"。本文关注的是**多类负载共用一个槽**。方案是分通道，而不是单纯加大槽数。

### F9 内存构成：RSS 的大头在托管堆之外（P2）

- 报告 4.6 的 RSS 为 1.5–2.0 GiB。本次复核（§5）：峰值时刻 RSS 的 77–95% 不在 .NET 托管堆。常规日志（约 400 B/行）时托管堆峰值为 183–338 MB；4 KB 正文时达到 825 MB。
- **候选**：DuckDB 的实际分配（`memory_limit` 不是进程 RSS 上限，见 [内存复盘](memory-root-cause.md)）、glibc 多 arena、GC 保留段。
- **缺口**：`StorageMetricsService.cs:55-67` 每 15 秒采样 `duckdb_memory()`，但压测只读取 `/metrics`，未记录该值与 WAL 大小。
- **方向**：先补采样（PF01、PF02），再做 `MALLOC_ARENA_MAX` 与 GC 对照（PF03）。字节预算只对大正文是主因（见 PF11）。

### F10 链路天表的 trace_id ART 索引（P2，待实测）

- `TimeTables.cs:109` 为每个链路天表建 ART 索引；点查 `TraceStore.cs:131` 依赖它。
- 报告中 traces 写入（8 并发）约 6 万行/秒，低于 metrics 的 24–43 万行/秒。但两者行宽与 JSON 列数不同，不能直接归因于索引。
- **参考**：内存复盘的实验显示，ART 索引在高频删插的汇总表上使文件增至约 130 MB（无索引约 4 MB）。链路天表是追加写，影响需要 A/B 实测。

### F11 指标查询对每行解析 labels JSON，并按 labels 字符串分组（P2，中期）

- `MetricStore.cs:94, 158` 的 `json_extract_string(labels, …)`；`MetricStore.cs:104` 的 `GROUP BY name, labels, bucket`；`PromqlSqlTranslator.cs:69-72` 形式相同。
- **影响**：指标查询成本随数据量线性增长。根治需要 series 维度化，这是 schema 变更（决策 D6）。

### F12 OTLP 请求体多次整份复制，span event 属性二次解析（P2）

- `OtlpV1Controller.cs:96-107`：`MemoryStream` → `ToArray` → 解压 → `ToArray`，JSON 分支再 `GetString`。`:138-145` 的解压路径还会复制一次。
- `OtlpProtoParser.cs:402`：span event 的属性先转 JSON 文本，再 `JsonDocument.Parse(...).Clone()`，最后写回 writer。

### F13 自身遥测与业务共用缓冲，且每条 span 重复序列化 resource（P2，需策略决策）

- 自身链路与指标固定写本机缓冲（`AGENTS.md` 第 5 节）。`LocalTraceExporter.cs:21-45` 每个 Activity 产生一条 span；`:40-44` 每条都重新序列化同一份 resource。
- 报告 4.5：batch=1 时自身 span 与业务行几乎等量，大批量时可忽略。
- **可做的小改动**：缓存 resource JSON，不改变策略。隔离或采样属于策略改动（D3）。

### F14 日志关键词检索为时间窗内的 LIKE 扫描（观察项，暂不优先）

- `LogStore.cs:82` 的 `body LIKE`。[store/sdd.md](sdd.md) 决策 4 已记录基准：100 万行 7 ms，1000 万行 41 ms，并明确暂不做行级倒排。列为观察项，不进入本期方案。

## 5. 压测报告复核（逐秒原始数据，只读）

**数据**：`results/full/scenarios/*/server_timeline.csv`（字段包括 `gc0/gc1/gc2`、`uptime_s`、`buffered`、`processed_*`/`dropped_*`、`process_mem_mb`、`gc_heap_mb`）与 `client_query_timeline.csv`。

**计算方法**：GC 爆发 = 同一秒内 `gc2` 增量 ≥3；查询尖峰 = 客户端 p99 ≥300 ms；零落库秒 = `processed_log` 增量为 0；托管外内存 = `process_mem_mb − gc_heap_mb`；查询尖峰与 GC 的对齐窗口 = 查询秒前 0.5 s 至后 1.5 s。

- **GC 节奏（已确认）**：饱和写入的各场景中，gen2 爆发约每 5 秒一次，相位稳定（`uptime_s mod 5`），爆发秒伴随约 22–25 次 gen0。gen2 平均 0.6–0.8 次/秒，对这个负载偏高。
- **GC 不足以解释 6 万行/秒上限（已确认）**：60k 场景下，爆发秒吞吐 3.6 万/秒，其他秒 5.1 万/秒；c64 场景则是 6.3 万对 4.6 万。两者方向相反。
- **GC 与查询尖峰无关（已确认）**：长稳测试中 40 个 p99 ≥300 ms 的秒，有 14 个落在 GC 对齐窗口内；普通秒为 334/857，比例相当。尖峰也没有 10/15/30/60 秒的固定相位。因此报告推测的"检查点节奏"与本文怀疑的"30 秒 Rollup tick"**均未被这份数据证明**。
- **零落库秒（已确认）**：60k 开环的 31 秒中有 7 秒落库为 0，同时缓冲升到约 7 万条。写入线程确有整秒停顿，原因未定。
- **5 秒节奏的来源（未定位）**：候选为 `ResourceSampler.cs:39`、OTel 批导出（默认 5 秒）与 `AlertDeliveryWorker.cs:16`。按代码估计，三者单次分配都不足以触发每秒二十多次 GC。需要 dotnet-trace 抓取分配栈确认（PF01 之后）。
- **内存构成（已确认）**：峰值时刻 RSS 的 77–95% 不在托管堆；常规日志时托管堆峰值 183–338 MB。
- **报告 6.2 建议的复核**：
  - 缓冲改字节上限：对大正文正确，对常规日志不是主因。
  - `DOTNET_gcServer=0`：文档已列为可选项（`deployment/sop.md:44`），在 PF03 中对照。
  - 容器限额提到 3–4 GiB：运维决策，不在本方案范围内。
  - WAL 落盘（持久化语义）：涉及语义变更，列为 D7，本期不做。
  - 报告未测试 `threads` 与 `MaxConcurrentQueries`。这两项是成本最低的实验。

### PF03 配置矩阵（2026-11-21，临时当前 HEAD 实例）

矩阵负载：40k logs/s、batch 1000、并发 16、分析查询并发等于 `MaxConcurrentQueries`、35 秒、预热 5 秒。候选复跑只重复 2线程/1槽、2线程/2槽、4线程/2槽。

| Threads | MaxQueries | 写入 rows/s | 写入 p99 ms | 查询 QPS | 查询 p99 ms | 峰值进程 MB | 峰值 GC heap MB | 一致性 |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| 2 | 1 | 26,067 / 27,900 | 37.22 / 467.07 | 46.40 / 5.17 | 213.21 / 488.44 | 726.4 / 1086.0 | 229.3 / 241.2 | 首轮及复跑均不稳定 |
| 2 | 2 | 40,000 / 40,000 | 29.36 / 31.28 | 43.10 / 11.97 | 168.28 / 552.07 | 729.3 / 827.1 | 276.4 / 310.7 | 两轮通过 |
| 2 | 4 | 38,833 | 33.34 | 48.53 | 352.00 | 1060.1 | 231.8 | 首轮不稳定 |
| 4 | 1 | 40,000 | 29.37 | 17.40 | 317.60 | 665.2 | 268.2 | 通过 |
| 4 | 2 | 40,000 / 40,000 | 30.67 / 18.29 | 27.77 / 14.87 | 176.11 / 489.09 | 717.8 / 859.6 | 136.5 / 194.3 | 两轮通过 |
| 4 | 4 | 40,000 | 31.50 | 37.90 | 529.06 | 824.6 | 122.5 | 通过 |
| 8 | 1 | 40,000 | 30.32 | 15.50 | 272.90 | 930.7 | 170.5 | 通过 |
| 8 | 2 | 40,000 | 27.42 | 23.90 | 388.40 | 952.2 | 114.4 | 首轮不一致 |
| 8 | 4 | 40,000 | 32.22 | 35.77 | 469.71 | 724.6 | 276.9 | 通过 |

- **默认值提案**：暂定 `threads=4`、`MaxConcurrentQueries=2`。候选复跑下 4/2 两轮写入均 40k rows/s、零丢弃、落库一致性通过，写入 p99 为 30.67/18.29ms，查询 p99 为 176.11/489.09ms；相比 2/2 的复跑，查询 p99 与峰值内存更优且写入更稳，保留 2/1作为回滚基线。
- **未完成**：`MALLOC_ARENA_MAX=2`、`DOTNET_gcServer=0`、压缩与 CHECKPOINT 对照未测；本表不证明生产 1 小时零丢弃或容器 OOM 下限。

## 6. 优化方案

### 6.1 原则

1. **先观测后改动**：PF01–PF03 不改变行为，产出的数据决定 F4、F9、F10 的取舍。
2. **保留内存安全边界**：有界并发、流式 LIMIT、128 MB 回归（见 [内存复盘](memory-root-cause.md)）全部保留。优化手段是缩短持有时长、缩小覆盖范围，而不是增加并发。
3. **每项改动配套回归与压测对照**；行为变化同步 bdd / tdd / sop（DOC-STANDARD 变更流程）。
4. **不做 CQRS / MediatR 微优化**（按需求排除）。

### 6.2 阶段与任务

任务卡细节（文件、内容、验收）以 [plan/TASKS.md](../plan/TASKS.md) 的 PF 组为准，本节只给顺序与依赖。

| 阶段 | 任务 | 内容 | 依赖 / 决策 | 规模 |
|---|---|---|---|---|
| 0 观测与实验 | PF01 | 写入分段耗时、锁等待、维护耗时、GC 暂停与提交内存、duckdb 内存与 WAL | — | M |
| 0 | PF02 | 压测场景补齐（指标稳态 + Rollup、保留归档、MQ、traces A/B）与采样字段 | PF01 | M |
| 0 | PF03 | 配置 A/B 矩阵（threads、MaxConcurrentQueries、MALLOC_ARENA_MAX、GC、DuckDB 压缩与检查点设置） | PF02 | S |
| 1 维护任务（P0） | PF04 | Rollup 增量化、迟到窗口、聚合降本（去掉逐行哈希） | — | M |
| 1 | PF05 | 独立维护门（并发 1），承接 Rollup、归档、保留的重活 | PF01、PF04、**D1** | L |
| 1 | PF06 | 归档 COPY 与词项索引移出写门（只读冻结表） | PF05 | M |
| 1 | PF07 | 保留交接锁收窄；S3 删除出锁；CHECKPOINT 移出交接锁 | — | S |
| 2 写入与入口（P1） | PF08 | 批段入缓冲、去掉 JSON 双转换、resource 缓存、批列表复用 | PF01、PF03 | L |
| 2 | PF09 | MQ 批量确认流水线；写入器有等待者时立即刷写 | PF01、**D4** | L |
| 2 | PF10 | 多连接并发追加的语义与吞吐验证（仓库外实验） | PF03 | S |
| 2 | PF11 | HTTP 429 与解析前余量判断；OTLP 整批预检；字节预算 | **D2、D5**、PF08 | M |
| 2 | PF12 | OTLP 请求体一次读入；span event 属性直接写入 writer | — | S |
| 3 查询并发（P1） | PF13 | 查询 / 告警 / 概览分通道；概览计数缓存 | PF05、**D7** | M |
| 3 | PF14 | trace_id 索引 A/B 与策略 | PF02 | M |
| 4 中期 | PF15 | 指标 series 维度化 | **D6**、PF03 | L |
| 4 | PF16 | 自身遥测隔离或采样（策略执行）与 resource 缓存 | **D3** | S |

### 6.3 验收目标（提案，PF03 结果后修订）

| 指标 | 当前基线（报告 / 本文复核） | 第一里程碑（提案） |
|---|---|---|
| 日志无丢弃上限（约 400 B/行） | 约 6 万行/秒 | ≥ 9 万行/秒，且 15 分钟长稳零丢弃 |
| Rollup 写门持有 p99 | 未测（按扫描行数推算，待 PF01） | < 50 ms |
| 归档期间写入 | 未测 | 零丢弃；写入 p99 不高于基线 +20% |
| 保留交接锁持有 p99（不含 S3 删除） | 未测 | < 100 ms |
| 分析 SQL（logs-agg）QPS | 约 7 | ≥ 14，且 tracked 内存不超过 `memory_limit` |
| 明细检索 p99（分析并发 2） | 未测（单独时 p99 为 17.8 ms） | ≤ 100 ms |
| 1 KB / 4 KB 正文饱和 10 分钟 | 1 KB、2 KB、4 KB 均发生 OOM | 无 OOM；托管堆由字节预算约束 |
| MQ 单实例（100 条/消息） | 未测（代码推算约 450 行/秒） | ≥ 1 万行/秒（Kafka 集成窗补验） |

### 6.4 决策点（需用户确认；确认前相关卡不得开工）

| 编号 | 决策 | 建议 | 影响卡 |
|---|---|---|---|
| D1 | 维护任务是否可以与写门、查询门并行（独立维护门，并发 1），调整"查询门→写门"的串行顺序 | 已实现；PF02/PF03 内存与吞吐数据仍作最终校验 | PF05、PF06 |
| D2 | JSON 接入缓冲满时是否统一返回 429（与 architecture / ingest 文档一致） | 同意；客户端兼容性写入发布说明 | PF11 |
| D3 | 自身遥测能否隔离到低优先级通道或采样（涉及 AGENTS 第 5 节） | 先只做 resource 缓存；隔离与采样待定 | PF16 |
| D4 | MQ 确认能否按批进行（仍为 at-least-once） | 同意 | PF09 |
| D5 | 缓冲字节预算的默认值与 429 阈值 | 建议从 256 MiB 起，用 PF02 数据校准 | PF11 |
| D6 | 指标查询是否纳入 series 维度化（schema 变更，影响 C6） | 中期项，PF03 之后再定 | PF15 |
| D7 | DuckDB `threads` / `MaxConcurrentQueries` 默认值是否调整 | PF03 矩阵与候选复跑暂提议 4线程/2查询槽；当前 2/1 保持回滚基线，待更完整生产长稳后切换默认值 | PF13 |

### 6.5 首批可直接认领的任务

无决策项 PF01 仍需 A/B 验收；PF07 代码与故障回归已实施，等待 PF02/PF03 持锁 p99 数据。PF12 已实施且相关定向测试通过，CPU 对照待测。PF04 已完成水位更新故障注入及 DuckDB 管理器重启后的恢复验证，但真实进程强杀与压力验收仍待完成。其余任务在依赖实验或对应决策登记后再认领。

## 7. 风险与回滚

- **内存**：提高并发（threads、MaxConcurrentQueries、维护门）可能突破 `memory_limit` 的实际覆盖面。缓解：每项先在 128 MB 回归与 DuckDbResourceTests 下验证；配置项保留回滚开关。
- **一致性**：Rollup 水位与 DELETE / INSERT 必须同事务。归档台账的幂等顺序不变（COPY → 行数校验 → 索引 → 台账 → DROP）。
- **语义**：429 与批量确认会改变客户端可见行为，需先发布说明并保留配置开关（D2、D4）。
- **模式变更**：PF15 涉及 schema 与 C6 rollup 表，需要幂等迁移（SchemaReconciler / 内嵌 DDL）。
- **不做**：不改变 HTTP 接入的持久化语义（仍为内存缓冲，持久化见 D7 之外的后续决策）；不做 CQRS 微优化；不改 SQLite。

## 8. 对现有文档的影响

- **初版**：新增本文并在 docs 导航、store SDD、TASKS 与 STATE 登记；当时未改代码。
- **2026-11-21 实施**：PF04/PF07/PF08/PF09/PF12 的已完成切片与测试结果同步到代码、对应 BDD/TDD/SOP/SDD 及本审计；未完成的任务卡保持进行中，不把未测的吞吐提升写作既成结论。

## 9. 变更记录

- 2026-11-21：初版。依据压测报告、代码静态审阅与逐秒数据复核；未改代码。
- 2026-11-21：完成若干无需决策的性能切片；OTLP 请求体与事件属性降本，补充局部回归场景。未重跑外部压测，不宣称吞吐目标已达成。
- 2026-11-21：PF01 在临时启动的宿主上完成真实 `/metrics` 抓取；HTTP 200、`text/plain`，核心观测指标存在，9 个 Store/摄取/维护直方图均有 9 个单调桶且 `+Inf` 等于 `_count`。未据此推断性能提升；A/B 对照仍待专用压测。
- 2026-11-21：PF04 增加水位更新约束故障注入；确认最后桶替换与水位同事务回滚，重开 DuckDB 管理器仍保留旧状态，移除故障后重试恢复。此项不替代进程强杀或性能压测。
- 2026-11-21：PF07 把 S3/本地冷对象删除移出源切换锁；删除期间隐藏过期候选并保留台账非空语义，阻塞的对象删除不妨碍新查询租约，失败后恢复可见与下轮重试。新增回归与三段保留类共 17/17；持锁 p99 待 PF02/PF03 压测。
- 2026-11-21：PF05 建立并发 1 的维护门，Rollup、归档、保留共享维护准入；维护等待新增直方图，取消与摄取互不阻塞。Archive/Rollup/Resource 定向共 40/40；低内存 128 MB 长稳与外部压测仍待验。
- 2026-11-21：PF06 将归档 `COPY`、词项索引与归档 `CHECKPOINT` 改为维护门持有下的克隆连接执行，不再获取写门；归档与生命周期回归 18/18。真实 S3 上传与持锁 p99 待压测。
- 2026-11-21：PF08 完成简单 JSON 批量映射与缓冲提交切片，日志、链路、指标、遥测四类按批入 Channel，并保留逐项 accepted/dropped；共享字典 JSON 序列化选项避免重复构造。新增批量回归 5/5（含 OTLP 定向）与 Store 缓冲定向 18/18；原始 JSON 片段直传、字节预算和 CPU 压测未做。
- 2026-11-21：PF09 完成批量入缓冲拒绝记录回填切片；批量 API 返回 rejected 列表，MQ 回压只重试拒绝项，不重复已接受记录。MQ 解码与阻塞 37/37、Store MQ 持久化 13/13；Kafka/RabbitMQ/Pulsar 消费者端批量确认与真实 broker 压测待集成窗。
- 2026-11-21：PF11 完成接入字节预算与 HTTP 429 切片；默认 256 MiB，简单 JSON/OTLP/ES Bulk 超预算返回 429 + Retry-After，OTLP 整批在解析前预检。Ingest 全量 140/140、MQ持久化定向14/14；大正文真实长稳仍待 PF02。
- 2026-11-21：PF02 增加 `pf02-plan.json` 与逐秒 DuckDB/WAL、维护、Rollup/归档/保留计数采样列。远端既有镜像短时 metrics+Rollup 场景 20k rows/s 零丢弃、p99 21.3ms、排空 3.3s、峰值进程内存 1153.5MB、峰值 GC heap 102.1MB、零探针失败；该证据不代表当前 HEAD 部署或一小时长稳。一小时指标、归档/保留、MQ、trace-index A/B 仍待对应集成窗。
- 2026-11-21：PF03 完成 threads 2/4/8 × max_queries 1/2/4 短时混合矩阵及候选复跑；暂提议 4/2，1 槽组合不稳定，8线程未提供一致优势。MALLOC_ARENA_MAX、DOTNET_gcServer、压缩与检查点对照未测。
- 2026-11-21：PF10 完成 DuckDB.NET 1.5.6.1 仓库外并发 Appender 实验；threads=2/4、workers=1/2/4/8、CHECKPOINT 干扰与数据核验均有结果。多连接未提供稳定吞吐收益，结论维持单写循环，不拆多 worker。
- 2026-11-21：PF14 完成 100 万行 trace_id 索引 A/B：无索引插入 664237 rows/s、点查 p99 6.414ms、文件10.0MB；有索引621072 rows/s、点查 p99 1.022ms、文件28.8MB；策略保持热表索引、冻结快照删除索引。
- 2026-11-21：PF16 完成自身遥测标签缓存：LocalTraceExporter 静态复用 resource JSON，LocalMetricExporter 缓存重复标签组合 JSON；SelfTelemetryTests 39/39。指标 series 维度化 PF15 因 D6 schema 决策暂不实施。
- 2026-11-21：PF13 增加告警专用查询槽并缓存总览今日行数 5 秒；告警定向测试与 Monitor 定向测试各通过，普通查询槽不再受告警单槽占用。
