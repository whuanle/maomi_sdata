# store — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| 性能：写入表名缓存 / 日志分页下推 / 总览批量 count | `LogSourceTests.QueryAsync_OffsetPaging_ReturnsSecondPage`、`QueryOverviewCommandHandlerTests`、`AlertPerformance_MixedSearchAndIngest_Sample` | 2026-11-20：Store 全量 305/305、Monitor 21 通过 1 平台跳过；分页回归专项 1/1；基线样本（10 万行，threads=2）搜索 P95 约 3–4ms、摄取约 3.5–6.9 万行/秒（单机抖动大，非严格 AB 对比）。未改动运行实例 |
| @STORE-S36–38 根因与持续运行 | `DuckDbMemoryRootCauseTests`、`RollupTests`、`DuckDbResourceTests`；[复盘及实验](memory-root-cause.md) | 2026-11-20：Store全量304/304、专项24/24，补充事务插入失败回滚后聚合专项10/10；Ingest130/130；Monitor首轮20通过1失败1跳过（MqRuntime并发枚举越界），复跑21通过1平台跳过；前端相关20/20及typecheck通过。未部署/未访问业务库，测试不替代长期生产压力验收 |
| @STORE-MEM1/MEM2 内存热更新及启动恢复 | `DuckDbMemorySettingsTests`、`StoreInitializerTests.Start_PersistedMemoryLimit_AppliesBeforeBootstrap`、`DuckDbSettingsIntegrationTests` | 2026-11-20：Store全量293/293，真实DuckDB参数绑定/已有与新连接共享/调小/线程不变通过；隔离HTTP及重启恢复1/1通过，Monitor21通过1跳过。未部署或改动运行实例 |
| @STORE-S31–35 | `DuckDbResourceTests`、`MqPersistenceTests`、`LogSourceTests`：并发预算/取消、写门、列缓存、真实低内存及失败重试 | 2026-11-20：专项23/23、查询/聚合/归档扩展90/90通过（临时构建入口排除未完成的SettingsTests）；完整Store运行出现既有取消异常类型断言失败后长期未完成并停止。Ingest/Monitor受GetDuckDb测试替身编译阻塞；未重启或做生产压力验收 |
| @STORE-ARCH7/8 一天 UTC 边界、S3 目录分支 | `ArchiveTests.Archive_OneDay_ArchivesYesterdayButNotToday`、`Archive_S3Destination_SkipsLocalDirectoryPreparation`；归档/设置专项 18/18、Store 全量 146/146 | 通过（2026-11-18）；首次测试发现夹具 UTC 日期隐式本地转换已修正；真实 S3 上传仍待部署登录后验收 |
| @STORE-AGENT1 | `StoreIntegrationTests.AgentAnalysis_QueriesColdFamiliesAcrossUtcMidnight`、`AgentAnalysis_RealHttpMcpCallsUseDuckDbAndRejectUnsafeRequests` | 2026-11-20：六类本地冷热/跨日和真实HTTP MCP通过；Store最终256/256；真实S3仍待验收，详见[Agents验证](../agents/tdd.md) |
| @STORE-S30 | `AnalyticsSearchSqlTests.AlertCount_*`、`DuckDbCancellationTests`；[告警验证](../alerts/tdd.md) | 通过（2026-11-19）；仅本机热数据样本，不代表生产零影响 |
| @STORE-RES1–3 | `StorageMetricsTests`（真实临时库、磁盘匹配、S3 分页/失败/超时/配置切换） | 2026-11-18 资源专项 7/7 通过；Store 全量 138/139，指标聚合预期 12/实际 11.25 的失败保留 |
| @STORE-S1 | `tests/SData.Store.Tests/StoreIntegrationTests.Log_WriteThenQuery_RoundTrip` | 通过（2026-11-15） |
| @STORE-S2 | `StoreIntegrationTests.Trace_WriteThenGetTrace_RoundTrip` | 通过（2026-11-15） |
| @STORE-S3 | `StoreIntegrationTests.Metric_WriteThenAggregate_RoundTrip` | 通过（2026-11-15） |
| @STORE-S4 | `StoreIntegrationTests.Telemetry_WriteThenQuery_RoundTrip` | 通过（2026-11-15） |
| @STORE-S5 | `StoreIntegrationTests.Analytics_RejectsWrite_AllowsSelect` | 通过（2026-11-15） |
| @STORE-S6 | 手工验证（可写 e2e 脚本补充） | 未自动化 |
| @STORE-DSL1–4 | `QueryDslTests`（26 例）+ `QueryDslExecutorTests`（4 例） | 通过（2026-11-15，T310/T311） |
| @STORE-DSL5 | `QueryDslTests` 比较操作符 4 例（数值 gt/lte 绑定、JSON TRY_CAST、字符串拒绝、时间 lt）+ `Execute_MetricsValueGt` | 通过（2026-11-16，统一搜索页） |
| @STORE-ARCH1–3 | `ArchiveTests`（3 例）+ `tests/SData.Database.Tests/ColdArchiveTests` | 通过（2026-11-15，T512） |
| @STORE-COLD-E2E | MinIO 真实 S3 归档一致性 + S3 超期对象清理（清单见 sop 冷存储对接） | e2e 待集成窗（T513；本地冷目录路径已由 ArchiveTests 覆盖） |
| @STORE-ARCH4 | `ArchiveTests.Archive_SortedParquet_WithId_AndTermIndexMirror` + `ColdTermIndex_ExtractAndKeyRules` | 通过（2026-11-16，T514/T516） |
| @STORE-ARCH5 | `ArchiveTests.Archive_HourlyGranularity_ShardedObjectsAndLedger` | 通过（2026-11-16，T517） |
| @STORE-COLD1 | `TableResolverTests.Resolve_LedgerRows_EmitPerDayColdFiles` + `Resolve_NoS3_NoLedger_LocalColdDirFallback` | 通过（2026-11-16，T515） |
| @STORE-COLD2 | `ColdCacheTests.Warm_LocalObject_HitThenLruEviction` | 通过（2026-11-16，T515） |
| @STORE-COLD3 | `TableResolverTests.Resolve_Keyword_PrunesColdFilesByTermIndex` | 通过（2026-11-16，T516） |
| @STORE-COLD4 | `TableResolverTests.Resolve_ConfiguredUnreachable_FailOpenSkipsColdGlob` | 通过（2026-11-17；原 `Resolve_Configured_EmitsDomainGlob` 按验存 fail-open 语义重写）。零命中分支与验存异常分支共用同一跳过逻辑；真桶空桶验证归入 sop 冷存储对接清单第 3 步（e2e 窗） |
| @STORE-COLD6 | `TableResolverTests.Resolve_ProbeBroken_CircuitSkipsRepeatProbesWithinWindow` | 通过（2026-11-17；验存探测按窗口缓存 + 异常熔断，修复 S3 故障时逐查询付探测延迟导致搜索页极慢） |
| @STORE-COLD5 | `StoreInitializerTests.Start_PersistedS3Override_RebuildsRuntimeSecret` + `Start_NoS3_NoSecretCreated` | 通过（2026-11-17；修复设置页 S3 配置重启后 DuckDB SECRET 丢失 → 查询走默认 AWS 端点报 NoSuchBucket） |
| @STORE-ARCH6 | 同 @STORE-COLD-E2E（MinIO 集成窗） | e2e 待集成窗（T518） |
| @STORE-MQ5 | `MqRetryNotificationTests.QueueCooldownSurvivesWorkerRestartAndStageChanges`、`NotificationSchemaTests` | 冷却持久化/阶段去重/失败预算/渠道停用及旧库补表通过（2026-11-20）；无真实通知外发 |
| @STORE-MQ4 | `MqPersistenceTests`（旧表重复补列、诊断往返、writer 安全故障与恢复） | 通过（2026-11-19，消费存储专项 5/5） |
| @STORE-MQ3 | `MqPersistenceTests.ConsumptionSamplesRoundTripAcrossDaysWithBoundParameters` | 通过（2026-11-19，消费存储专项 4/4）；本次未执行真实 broker 故障注入 |
| @STORE-MQ1 | `MqPersistenceTests`（3 例） | 回执提交、真实并发满载与取消通过（2026-11-18） |
| @STORE-JSONDISC | `AnalyticsSearchSqlTests`（FacetKeysSql/FacetValuesSql 抽样与转义 + JsonNestedPathParam 互不串查 / ChildKeysSql 子属性枚举 / JsonProjectionQuotedAlias 列别名，真实 DuckDB 28/28） | 通过（2026-11-18，支撑 logs @LOGS-UI-JSONDISC1/2） |
| @STORE-MQ2 | `LogSourceTests.LegacyLogTable_MigratedWithSource`（迁移后 Appender 追加） | 通过，定向回归 7/7（2026-11-18） |
| MQ 最终回归 | Store 128/128；`Metric_WriteThenAggregate_RoundTrip` 曾在全量中偶发失败，单项及全量复跑通过，原因未定位 | 如实保留偶发记录（2026-11-18） |
| @STORE-S8 | `AnalyticsSearchSqlTests.SessionsToken_ExecutesReadOnlyBoundQuery` | 通过（2026-11-18，Store 全量 132/132） |
| @STORE-S9 | `dotnet watch --project src/SData/SData.csproj --list` + MSBuild Content/None 元数据核对 | 通过（2026-11-19）：数据目录不在监听列表，12 个数据文件均为 Watch=false，Program.cs 仍被监听，appsettings 默认元数据不变；未启动服务实测 |
| 回归基线 | `dotnet test tests/SData.Store.Tests` 120/120 + `SData.Database.Tests` 17/17 | 通过（2026-11-17） |
| 四类归档、迟到补报、失败重试与重启恢复 | `ArchiveLifecycleTests.cs` + Store 全量；设置页/API 测试 | 2026-11-19：Store 153/153、Metrics 46/46，前端相关 7/7，前端构建通过；未重启部署新后端，真实 S3 四类验收待部署 |
