# 存储布局（SQLite + DuckDB）

## SQLite（元数据，`SData:Database:Path`，默认 data/sdata.db）

| 表 | 用途 |
|---|---|
| `user` | 本地账号（PBKDF2 密码哈希，admin 种子来自配置） |
| `ingest_key` | 接入 API Key（仅存 SHA256，种子来自 `Ingest:InitialApiKey`） |
| `device` | 设备台账（device_key 唯一，标签 JSON） |
| `dashboard` | 仪表盘配置（config_json 内含 panels 数组；历史 screen 保留但不再提供功能） |
| `saved_query` | 保存的分析查询 |
| `setting` | 系统键值 |

- 建库：`EnsureCreated`（幂等，**对已存在的库不补新表**）+ `SchemaReconciler`（只增自愈：整表缺失按 EF 模型补建 CREATE TABLE/INDEX——修复老库缺后加实体表的缺口（如 `ColdArchives`/`rum_project`），缺列 ALTER TABLE ADD COLUMN 修补）+ 种子（admin / 默认 key）。
- Schema 工具：`dotnet run --project tool/SchemaSync -- script|apply|diff`。

## DuckDB（观测数据，`SData:DuckDb:Path`，默认 data/sdata.duckdb）

### 按天分表（写入器自动建表 + 索引）

logs/traces 自 2026-11-23 起为「视图门面」天对象：物理数据在 data 窄表（resource 字典编码为 resource_id），resource 原文一天一行存于 res 字典表；`logs_yyyymmdd` 本身是暴露旧宽列序的视图，查询层（LogStore/QueryDsl/冷层 parquet union/UI facet SQL）零改动。旧版本升级前遗留的物理宽表原样共存，写入端按 `SELECT * LIMIT 0` 的列布局自适应（宽表写 resource 原文，窄表写 resource_id）。

| 表 | 列 |
|---|---|
| `log_data_yyyymmdd` | id, ts, service, severity, body, trace_id, span_id, attributes(JSON 字符串), resource_id(BIGINT → log_res 表), source（ES Bulk 索引名，T121） |
| `log_res_yyyymmdd` | id(BIGINT), resource（同一天内 distinct resource 原文，一行一条；ResourceInterner 按天去重，雪花 id 跨重启唯一） |
| `logs_yyyymmdd`（视图） | id, ts, service, severity, body, trace_id, span_id, attributes, resource, source = `log_data LEFT JOIN log_res ON resource_id`（与旧宽表列名/列序一致） |
| `trace_data_yyyymmdd` | trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource_id（trace_id 建 ART 索引，开关 `SData:Ingest:TraceIndexEnabled`） |
| `trace_res_yyyymmdd` | id, resource |
| `traces_yyyymmdd`（视图） | trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource = `trace_data LEFT JOIN trace_res` |
| `metrics_yyyymmdd` | name, ts, value, labels(JSON 字符串), kind |
| `iot_yyyymmdd` | device_key, signal, ts, num_value, str_value, quality |
| `mq_consumption_yyyymmdd` | ts, instance_id, session_id, rule, match_mode, state, received_messages/bytes, persisted_records, parse_failures, skipped_messages, confirmed_messages, confirmation_failures, sampling_gaps |

视图门面的配套规则：归档冻结把伴生物理表原子改名为 `log_data_archive_{guid}_{day}`/`log_res_archive_{guid}_{day}` 并以同名宽列视图快照（`logs_archive_{guid}_{day}`）暴露，归档 COPY/词项索引零改动；保留策略 DROP 视图连带 DROP 伴生表。冷层 parquet 仍为宽列格式（resource 原文），与本地天表视图 `UNION ALL` 列形状一致。

消费统计每 10 秒经统一缓冲批量写入，按消费时间而非日志事件时间分表；累计值按会话解释，不可逐样本求和。skipped_messages仅兼容保留，新采样固定0；当前阻塞消息数/最早阻塞时间在diagnostics中保存，不表示已确认或丢弃。沿用日志热保留周期，不参与冷归档，不将历史状态当作当前在线状态。

### 指标 rollup 汇总表（全局表，非天分表；契约 C6，T312）

| 表 | 列 |
|---|---|
| `metrics_rollup_5m` | name, labels_hash(md5(labels)), labels, bucket_ts, min, max, sum, count（索引 name+labels_hash+bucket_ts） |
| `metrics_rollup_1h` | 同上（同构） |

- 回填：`RollupService` 每 30s 一轮，窗口整删整插（幂等）：5m 表回填最近 60 分钟（滞后 ≤10 分钟）、1h 表回填最近 4 小时（滞后 ≤2 小时）。
- 分派：`MetricStore` 跨度 > 24h 且聚合 ∈ {avg,sum,min,max,count} 走 `metrics_rollup_1h`（avg=sum/count 加权重算、步长下限 1h），p95/last 语义不可由 rollup 列推导、保持原始天表；rollup 表不受保留策略 DROP（`ParseDay` 对全局表返回 null）。
- DDL：内嵌 `DuckDb/Ddl/002_rollup.sql`（幂等），Schema 引导与 `tool/SchemaSync duck` 统一应用。

### 冷层查询（契约 C2，T510）

- `ITableResolver.Resolve(domain, from, to)` 返回本地天表集合 + 冷层 parquet 通配；实现 `TableResolver` 未配置 S3 时通配恒为空（查询行为与现状一致）；已配置 S3 时台账为空才走域级通配兜底，且输出前经 glob() 验存——零命中或端点不可达/鉴权失败/桶不存在一律跳过冷层分支（fail-open，保证空桶场景本地查询可用；read_parquet 对零命中通配在 prepare 阶段抛 IO Error: No files found）。
- 冷层对象布局（T512 归档写入遵循）：`s3://{Bucket}/{Prefix}/{domain}/**/*.parquet`（domain ∈ logs|traces|metrics|iot）；查询编译为 `SELECT ... FROM read_parquet('通配')` 与本地天表 UNION ALL，时间裁剪依赖 parquet 元数据统计。
- 执行链路：`QueryDslExecutorService` 注入 ITableResolver，本地与冷层皆空时短路返回空响应。

- 查询：时间范围映射到天表集合，`UNION ALL` 合并后外层过滤/排序/分页；标签过滤用 `json_extract_string(labels, '$.key')`（路径为参数绑定值）。
- 聚合：`epoch_ms(ts) // step * step` 分桶 + avg/sum/max/min/count/arg_max/quantile_cont。
- 保留：`RetentionService` 每小时 DROP 过期天对象 + `CHECKPOINT` 收缩 WAL（logs/traces 视图天连带 DROP 伴生窄表/字典表）；`Retention:{Logs,Traces,Metrics,Iot}`（天，0=永久）。
- 初始化 DDL 内嵌于 `SData.Store.Core`（`DuckDb/Ddl/*.sql`，幂等），运行时由 Schema 引导执行；`tool/SchemaSync duck` 可手动补跑。
- 运行参数：`memory_limit`/`threads`/`max_concurrent_queries`/缓存容量等性能参数由系统按环境（cgroup CPU 配额与容器内存上限）自动计算基线，**不再提供用户配置**（system.json 残留键被忽略，详见 [设置设计](settings/sdd.md)与监控页 Tuning 面板）；查询跨度上限 `MaxQueryDays`、分析查询预算 `QueryTimeoutSeconds` 仍为可配置策略项；`preserve_insertion_order=false` 固定启用。

## 写入路径

```
HTTP ingest ──→ IIngestBuffer（动态双闸门：条数容量 + 字节预算，随 AdaptiveTuningService 扩缩）
                     │ IngestWriterService（batch/flush 取 ITuningState，基线按环境自算）
                     ▼
        按（类型,天）分组 → logs/traces：ResourceInterner 去重 → 字典表新条目先落
                          → DuckDB Appender 批量写窄表（resource_id）；旧宽表天直接写原文
```

自适应调参（`AdaptiveTuningService`，10s tick）：缓冲利用率 ≥80%（或发生丢弃）连续 2 个 tick 且内存有余量（进程占用 + DuckDB 限额 + 缓冲预算 ≤ 环境内存 × 85%）时逐步扩容（×1.25，硬顶=环境内存推导）；持续空闲 5 分钟按 ×0.75 回缩到基线。当前生效值经 `/api/monitor/status` 的 `tuning` 段与总览页「自动调参」面板只读展示。

监控指标：`/metrics` 暴露 `sdata_buffer_count/sdata_ingest_processed_total/sdata_ingest_dropped_total{kind}`。

## 冷层与全文检索布局（T515–T518）

```
热层  DuckDB 天表 logs_/traces_/metrics_/iot_{yyyyMMdd}
        │ 超过 HotDays（ArchiveService，ORDER BY 主过滤键,ts）
        ▼
冷层  data/cold/{domain}/{shard}.parquet          ← 本地冷存（恒有副本；双写并存时与 S3 桶内结构同构）
      s3://{bucket}/{prefix}/{domain}/{shard}.parquet ← S3 冗余副本（配置 S3 后与本地并存；台账逐行路由介质）
      data/cold-idx/{domain}/{shard}.idx.parquet  ← 词项索引本地镜像（归档时构建，查询零网络；S3 同步一份备份）
      data/cache/cold/{domain}/{shard}.parquet    ← S3 对象本地缓存（ColdCacheService LRU 预热）
台账  SQLite ColdArchives（domain, day, objectKey, rowCount；一对象一行，day 为过期桶）
```

- **分片名**：day 粒度 = `yyyyMMdd`（默认）；hour 粒度 = `yyyyMMddHH`（`S3.ArchiveGranularity=hour`，空分片跳过）。索引/缓存/台账对象键与分片一一对应。
- **词项索引**：单列 `term`（ASCII token 小写 + CJK 相邻二元组）；查询关键词提取同规则 → 本地索引扫描求「词项全部存在」的候选分片 → 仅拉候选分片对象 → 对象内 LIKE 精确匹配（超集过滤，语义不变）。
- **参数**：`SData:S3:*`（含 ArchiveGranularity）、`SData:ColdCache:{Enabled,MaxBytes}`、`SData:DuckDb:{MaxQueryDays,ColdMaxQueryDays}`。

### 四类归档批次（2026-11-19）
- 新对象为 `{domain}/{batchId}/{yyyyMMdd[HH]}.parquet`，同日迟到数据使用新批次，旧日期对象继续可查。
- domain 增加 rum_session、rum_event、rum_replay，共用 Retention.Rum；只有 logs 构建正文索引。台账发布后强刷，已发布本地副本不重复读取；未发布批次保留查询和恢复能力。
- 空台账兜底仅匹配旧格式目录根部日期文件，不扫描新批次目录和索引，避免把未发布对象当成有效归档。
