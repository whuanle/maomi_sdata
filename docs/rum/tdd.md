# rum — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @RUM-S15 | `go/cmd/sdata/rum_policy_test.go::TestRumGlobalOriginCapAppliesToEveryPublicDataEndpoint`；`go/internal/config/rum_test.go` | 2026-11-22：通过；三个公开数据通道、全局与项目交集、非法上限拒绝、空上限及配置覆盖 |
| @RUM-S16 | `go/cmd/sdata/rum_policy_test.go::TestRumProbeServesOnlyRealArtifact` | 2026-11-22：通过；真实文件内容、缺失/非文件 503 |
| @RUM-S17 / @RUM-S18 | `go/internal/rum/live_test.go` 五个真实 WebSocket 测试 | 2026-11-22：连续 10 轮通过（3.170s）；协商/握手、JWT/来源、直接与 beacon 推送、单次入库、删除重检、退订、false 背压、队列上限、分段消息、ping/close、context 和关闭释放 |
| Go 并发验证限制 | `go test -race -tags duckdb_use_lib ./internal/rum -run RumLive` | 2026-11-22：未运行成功，现有 Zig 工具链链接缺少 synchronization 库；未安装依赖，普通真实套接字测试通过，不宣称 race 检查通过 |
| @RUM-S12 | `go/cmd/sdata/rum_handlers_test.go::TestRumProjectsOriginAndAdminContract`；`go/internal/rum/projects_test.go` | 2026-11-22：通过；覆盖管理员权限、来源规范化、Referer 回退、预检、采样值、键不可变和软删除；浏览器未验收 |
| @RUM-S13 | `go/cmd/sdata/rum_handlers_test.go::TestRumBeaconAndQueriesUseDuckDB` | 2026-11-22：gzip → 异步 DuckDB → 七类查询通过；跨日、同名跨项目、p75、分页、参数注入和 SQLite 无时序写入已验证 |
| @RUM-S14 | `go/cmd/sdata/rum_handlers_test.go::TestRumBackpressureAndEmptyQueryContract`；`go/internal/store/rum_test.go` | 2026-11-22：通过；行数/字节预算、429、空窗口、gzip 错误、队列快照和关闭排空；不等同于实时 Hub 验收 |
| Go 验证边界 | 五个 HTTP 测试、五个 SignalR 测试、两个存储、两个元数据和一个配置测试 | 2026-11-22：四包定向测试通过（HTTP 包 6.449s）；未执行本轮真实浏览器或断网重连验收 |
| @RUM-S1 | `ui/src/pages/rum/__tests__/RumPage.test.tsx` | 通过（2026-11-19，8/8；全量 UI 443/443） |
| @RUM-S6 / @RUM-S7 | `ui/src/pages/rum/__tests__/RumPage.test.tsx`：无项目直接创建、自动选中刷新、已有项目入口及 write/readonly 权限 | 通过（2026-11-19，会话页 12/12；typecheck/lint 通过）；全量 UI 520/521，SettingsPage 归档策略断言期望 hotDays=1、实际 30，本次未修改该模块；未执行本次浏览器验收 |
| @RUM-S8 | ClientSessionsPage 工具栏及错误/回放/排序筛选紧凑并排；会话页回归 | 会话页 14/14、typecheck 通过（2026-11-19）；本次不同窗口宽度的浏览器布局验收未执行 |
| @RUM-S9 / @RUM-S10 | `RumOriginPolicyTests.cs`、`RumCorsIntegrationTests.cs` | 通过（2026-11-19，RUM Store 32/32，HTTP/CORS/直接 WebSocket 与认证订阅集成 1/1） |
| @RUM-S11 | `RumPage.test.tsx` 允许站点保存、校验与权限；全量 UI | 通过（2026-11-19，会话页 14/14，全量 569/569，typecheck/lint/build 通过） |
| 来源保护最终构建 | 宿主隔离 OutputPath 构建、前端 build（含探针） | 通过（2026-11-19，宿主 0 编译错误；未重启现有后端，隔离验收服务已关闭） |
| 来源浏览器验收 | 隔离 SQLite/缓冲及两个随机端口站点；`.artifacts/rum-browser-harness/` | 通过（2026-11-19）：未允许来源 config/fetch/WS 被拦截且接收 0 条；允许来源 200/204/WS open、真实探针已加载且接收 4 条。未修改真实项目或重启现有实例 |
| @RUM-S2 | `RumStoreTests.Sessions_SearchAndReplay_AreWindowAndProjectScoped` | 通过（2026-11-18，RUM Store/Beacon 11/11） |
| @RUM-S3 | SDK `rum-sdk.test.ts` / `replay.test.ts` / `realtime.test.ts` | 通过（2026-11-18，21/21；SDK 类型检查通过） |
| @RUM-S4 | 5391 隔离实例真实探针、鼠标点击、分片解码与浏览器播放器 | 真实采集→列表→回放已通过，播放器末尾重现 Click recorded 页面变化（2026-11-19）；另已验证深链刷新恢复、暗色详情与 900px 亮色窗口；英文切换已确认；断网双端验收未完成，不记为通过 |
| @RUM-S5 | 5391 真实 HTTP：项目创建/查询、采样配置、readonly 查询与写入、超长窗口 | 通过（2026-11-18，200/200/配置匹配/200/403/400） |
| 构建 | 前端 typecheck/lint/build、后端隔离 OutputPath 构建 | 通过（2026-11-18）；默认后端输出被运行中服务锁定，未停止原服务 |
| 项目管理回归 | `SData.Database.Tests` 过滤 RumProject | 未执行成功：无关 `DeviceCredentialTests.cs:62` 构造参数编译错误（2026-11-19 重跑仍阻塞），不记为通过 |
| @RUM-B1 | `tests/SData.Store.Tests/RumBeaconTests.Beacon_GzipEvents_LandInTables` / `Beacon_PlainJson_LandsInEventTable` | 通过（2026-11-15，Store.Tests 40/40） |
| @RUM-B2 | `RumBeaconTests.Beacon_MissingKey_ReturnsInvalidKey` | 通过（2026-11-15） |
| @RUM-B3 | `RumBeaconTests.Beacon_BufferFull_ReturnsBufferFull` | 通过（2026-11-15） |
| @RUM-B4 | `RumBeaconTests.Config_RejectsUnconfiguredSources` | 通过（2026-11-19，严格模式拒绝未知项目和未允许来源） |
| @RUM-Q1 | `tests/SData.Store.Tests/RumStoreTests.Aggregate_AfterBeaconIngest_ProducesExpectedMetrics` | 通过（2026-11-15，RumStoreTests 2/2） |
| @RUM-Q2 | 同上（errors Top 断言 + 项目隔离） | 通过（2026-11-15） |
| @RUM-Q3 | `RumStoreTests.Aggregate_NoTables_ReturnsEmpty` + 会话列表断言 | 通过（2026-11-15） |
| @RUM-R1 | `tests/SData.Store.Tests/RumStoreTests.GetReplay_AfterChunkIngest_ReturnsOrderedChunks` | 通过（2026-11-15，Store.Tests 63/63） |
| @RUM-R2 | `ui/packages/rum-sdk/__tests__/replay.test.ts`（分段/seq/采样/回退/无工厂 6 例） | 通过（2026-11-15，ui test 21/21） |
| @RUM-R4 | Chart Explorer 会话样本详情复用 `ReplayView` 并传入执行区间 | 待完整浏览器回放验收（2026-11-18）；查询来源与参数由 Store 测试覆盖 |
| @RUM-R3 | ui /rum 回放 Tab（rrweb-player + 降级预览）冒烟 `src/pages/rum/__tests__/RumPage.test.tsx` | 冒烟通过（2026-11-15）；浏览器内完整回放待集成窗 |
| @RUM-H1 | `tests/SData.Store.Tests/RumStoreTests.Heatmap_And_Sitemap_AggregateClicks` | 通过（2026-11-15，RumStoreTests 4/4） |
| @RUM-H2 | 同上（页面树断言） | 通过（2026-11-15） |
| @RUM-H3 | `ui/src/pages/rum/__tests__/RumPage.test.tsx`：热力图类目轴与零点击过滤；前端 typecheck/lint、主应用与探针隔离构建 | 通过（2026-11-20，全量 UI 746/746，含 RUM 15/15）；未执行本次浏览器验收 |
| @RUM-L3 | `tests/SData.Store.Tests/RumStoreTests.Heatmap_DocumentCoords_PageFilter_And_TopTargets` | 通过（2026-11-16，Store.Tests 65/65） |
| @RUM-L4 | 同上（TopTargets selector 优先/targetId 回退/无目标不计 断言） | 通过（2026-11-16） |
| @RUM-L5 | `ui/packages/rum-sdk/__tests__/realtime.test.ts`（连接成功/失败回落/重连/stop 4 例）+ `rum-sdk.test.ts` 实时回落 1 例 | 通过（2026-11-16，rum-sdk 18/18，ui 31/31） |
| @RUM-L6 | `ui/packages/rum-sdk/__tests__/rum-sdk.test.ts`（projectKey/sessionId/坐标/selector 断言） | 通过（2026-11-16） |
| @RUM-L1 | ui `RumPage.test.tsx`（mock signalr 连接→注入 click 推送→点击流渲染冒烟） | 冒烟通过（2026-11-16）；浏览器内双端联通待集成窗 |
| @RUM-L2 | 冒烟（RumBeaconService.Dispatch → IRumLivePublisher 挂点，见 `RumBeaconService.cs` click 分支） | 代码评审通过（2026-11-16） |
| 四类归档、迟到补报、失败重试与重启恢复 | `ArchiveLifecycleTests.cs` + Store 全量；设置页/API 测试 | 2026-11-19：Store 153/153、Metrics 46/46，前端相关 7/7，前端构建通过；未重启部署新后端，真实 S3 四类验收待部署 |
