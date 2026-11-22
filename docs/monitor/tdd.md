# monitor — TDD（验证映射）

> 关联：[bdd](bdd.md) · [sdd](sdd.md) · [sop](sop.md) · 证据：`tests/SData.Monitor.Tests/`、`ui/src/pages/overview/__tests__/`

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @MON-GO1 | `go/cmd/sdata/monitor_parity_test.go` | 2026-11-22：真实临时库通过；两日日志天表=2，今日日志=1，会话=1，日志累计=2且不复制到其它域 |
| @MON-PF13 总览今日行数缓存 | `QueryOverviewCommandHandlerTests.Handle_CachesTodayRowCountsForTtl` | 2026-11-21：连续总览刷新复用今日行数查询；定向 Monitor 9/9 |
| @MON-PF01 GC、存储和 Store 耗时指标 | `MonitorControllerTests.Metrics_ReportsGcAndAvailableStorageSamples`、Store Rollup/归档/保留专项 | 2026-11-21：Monitor 22 通过、1 平台跳过；Store Rollup/归档/保留专项 14/14 通过；验证 GC/存储采样、锁和阶段/维护直方图及工作量计数。未启动宿主做 HTTP 抓取 |
| GC 分代回收次数指标 | `/metrics` 的 `sdata_gc_collection_count_total{generation}`、`ResourceSnapshot.GcCollectionCount0/1/2` | 2026-11-20：随宿主 Release 编译通过；Monitor 21 通过 1 平台跳过；与 OTel Runtime 标准 GC 指标互补 |
| 总览/状态今日行数批量统计 | `QueryOverviewCommandHandlerTests`（`StubStoreInfo.CountTablesAsync`）、Monitor 全量 | 2026-11-20：Monitor 21 通过 1 平台跳过；七域/四域 count 合并为单条查询，输出与逐表统计一致 |
| @MON-S8 持久化恢复后隐藏提示 | `LogConsumptionPage.test.tsx`：未恢复仍告警，已恢复整块提示及原因完全不渲染 | 2026-11-20：本轮消费页17/17、类型检查通过；前轮前端全量773/773，本轮未重跑全量或做浏览器/部署验收 |
| @MON-MQ1 消费运行态 | `tool/MqProducer/verify.py` 真实三类消费者 | 三实例 consuming、计数增长，服务重启归零后恢复；浏览器消费页已确认同一快照（2026-11-18） |
| @MON-S1 服务状态详情 | 既有 xunit（宿主运行验证）+ `/api/monitor/status` 手工核对 | 2026-11-15 既有实现回归绿 |
| @MON-O1 总览聚合通道健康 | `tests/SData.Monitor.Tests/QueryOverviewCommandHandlerTests.Handle_AggregatesChannelsTodayRowsAndStorage` | 2026-11-18 ✓ Monitor 5/5 |
| @MON-O2 总览聚合存储用量 | `QueryOverviewCommandHandlerTests`（热层及有效 S3 完整/不完整配置） | 2026-11-18 ✓ Monitor 5/5 |
| @MON-O3 总览页前端呈现 | `ui/src/pages/overview/__tests__/OverviewPage.test.tsx`（七域、系统、存储、无样本查询、双语） | 2026-11-18 ✓ Overview 6/6 |
| @MON-O4 队列实例安全摘要 | 同上及 `Handle_UsesEffectiveSettingsAndReturnsSafeConsumerSnapshots` | 2026-11-18 ✓ 后端 5/5、前端 6/6 |
| @MON-O5 刷新失败与旧快照 | Overview 测试（首次失败、后续部分失败） | 2026-11-18 ✓ 6/6 |
| @MON-S2 系统与应用资源 | `ResourceSamplerTests`（CPU 差分、首次采样、Linux 解析、cgroup 路径与配额） | 2026-11-18 ✓ Monitor 16/16 |
| @MON-S3 存储资源面板 | `StorageMetricsTests`、`ResourcePanels.test.tsx` | 2026-11-18 ✓ 资源面板 2/2，存储专项通过 |
| @MON-S4 macOS 资源 | `MacSystemResourcesTests`（CPU 溢出、4KB/16KB 页换算及 macOS 原生烟测） | 2026-11-18 Monitor 19 通过、1 跳过；当前 Windows，原生烟测待 Mac 实机 |

## 失败消息禁止跳过（2026-11-20）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @INGEST-MQ2 / @INGEST-MQ11、@MON-MQ5 | `MqMessageBlockingTests`、`MqRuntimeTests`、消费页测试 | Ingest129/129、Monitor21通过/1平台跳过、消费页21/21；构建通过（2026-11-20） |

- 解析失败、空载荷、零记录和部分失败均持有原消息退避，不写保底记录、不确认；完整解析提交后才进入确认阶段。Status 移除跳过和保底结果，展示阻塞数量与时间。旧统计表 skipped_messages 列仅兼容保留，新采样固定0。
- 前端全量731/732，未改动的 HeatmapExplorer 下钻/缩放测试失败，未称全绿。未执行真实broker故障注入、浏览器验收或服务重启。本节语义替代下列历史记录中的保底确认策略。

## 消费重试与限流通知（2026-11-20）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @MON-MQ7 / @MON-MQ8、@INGEST-MQ16 / @INGEST-MQ17 | `MqRetryNotificationTests`、`MqRuntimeTests`、消费页及通知请求测试 | Ingest 120/120、前端专项24/24、Monitor21通过/1平台跳过、通知旧库补表1/1、设置持久化专项1/1；类型检查、lint与构建通过（2026-11-20） |

- 默认5/10/20/40/60秒退避，之后固定60秒；立即重试需要确认且串行重建整个实例。未恢复失败优先于consuming，恢复历史不影响当前健康。
- 假发送器覆盖同队列跨阶段冷却、工作器重建、投递失败预算、不同队列独立及渠道停用；SQLite保存冷却，先预留后发送。恢复通知本期不发送；未做真实通知、broker故障注入、浏览器或部署验收。
- 最终前端全量727/727（103个测试文件）、构建通过；Ingest120/120、Monitor21通过/1平台跳过（2026-11-20）。

## 消费状态增量验证（2026-11-19）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @MON-MQ2 | `MqConsumptionTests`：配置过滤、历史会话隔离、范围校验 | Monitor 21 通过、1 平台跳过（2026-11-19） |
| @MON-MQ3 / @MON-MQ4 | `LogConsumptionPage.test.tsx`：Status、速率、解析警告、失败提示和协议详情回归 | 12/12 通过（2026-11-19） |

- 最终验证：Ingest 73/73、Monitor 21 通过/1 平台跳过、Store 227/227、消费页与概览 18/18；lint 0 错误/3 条仪表盘警告。前端全量 645/651，6 项失败位于 ChartPanelEditor/DashboardGrid；类型检查被 ChartPanelEditor 的 setParams/urlTab 和 PanelDesignerPage 的 Eye 未使用阻塞，构建未通过。本次未修改上述仪表盘文件。真实 broker 故障注入及新版页面浏览器验收未执行（2026-11-19）。

## 消费阶段诊断（2026-11-19）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @MON-MQ5 / @MON-MQ6 | `MqRuntimeTests`、`api/__tests__/monitor.test.ts`、`LogConsumptionPage.test.tsx` | 最终 Ingest 95/95、前端请求/消费组件 22/22、Monitor 21 通过/1 平台跳过；存储专项 5/5（2026-11-19） |

- 请求区分超时、HTTP、JSON 解析及契约错误；阶段失败保留原因/时间/恢复状态，原文保底按诊断 ID 关联；旧统计表 diagnostics 补列重复执行通过，writer 摘要不含原始异常。
- 本轮前端构建通过，最终类型检查通过；lint 0 错误/1 条设置页未使用导入警告。未重启运行服务，浏览器无可接管页面，截图对应刷新失败未复现、根因未确认。未执行真实 broker 故障注入或本轮全量回归。

## 消费速率与逐名称状态展示（2026-11-19）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @MON-S5 | `LogConsumptionPage.test.tsx`、`TopicConsumption.test.tsx`：多规则状态/错误隔离、前缀汇总、协议详情、禁用/无快照/刷新失败 | 消费页 13/13、状态组件 3/3 通过（2026-11-19） |
| @MON-S6 | `ui/src/utils/__tests__/format.test.ts`：长小数、整数、单位边界、极小正值和无效值 | 格式化 30/30 通过（2026-11-19） |

- 最终全量前端 699/702：3 项失败均在未修改的 `SettingsPage.test.tsx`（管理员卡片、加载与失败重试）。日志见 `.artifacts/log-consumption-ui-tests-final.log`。相关用例 46/46 通过。
- 类型检查首次通过；最终复验被未修改的 `ui/src/api/settings.ts:2` 两个未使用类型导入 `MqttAuthSettings`、`SaveMqttAuthSettings`（TS6196）阻塞。lint 0 错误、1 条未修改设置页 `IngestCard` 未使用警告。本次差异空白检查通过。
- 未执行真实浏览器、真实 broker 故障注入或部署验收；本次仅复用现有规则级快照，不修改消费连接或持久化机制。

## 消费失败红色警告（2026-11-19）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @MON-S7 | `LogConsumptionPage.test.tsx`、`TopicConsumption.test.tsx`：解析/确认失败红色标签、最近失败红色提示、健康消费绿色 | 19/19 通过，typecheck 与 lint 通过（2026-11-19） |

- 本轮未重跑前端全量测试，未执行真实浏览器验收；不改变消费运行状态及重连机制。

## 资源监控增量验证（2026-11-18）

- Monitor 最终 16/16，存储资源专项 7/7，Overview 8/8；此前前端全量 291/291、构建通过。
- 最终构建复验被其他未提交改动阻塞：`ChartExplorerPage.tsx:585,594` 的 TS2657/TS1005（JSX 语法），未修改该文件，当前工作区不能称全绿。
- Store 全量 138/139；`Metric_WriteThenAggregate_RoundTrip` 预期 12、实际 11.25，资源采样用例通过。未改动该聚合逻辑。
- 浏览器确认四组资源面板已渲染；运行中后端尚未加载新增采样字段，指标正确显示未知。图表页 JSX 错误触发全局开发错误遮罩，视觉验收受阻。未重启后端，真实资源联调及 Linux 实机验证未完成；S3 使用模拟客户端验证，未进行远端写入。

## 前次验证边界（2026-11-18，资源监控增量之前）

- 前端全量：253/257 通过；搜索页 `surroundingContext.test.ts` 与 `TraceDetailDrawer.test.tsx` 共 4 例失败，未改动这些已有工作。
- `npm run build` 未通过：`src/pages/search/TraceWaterfall.tsx:85` 的 ECharts `Marker.label` 类型不兼容（TS2322），未进入 Vite 构建。
- 补充 `/api/health` 兼容路由并新增路由测试后，Monitor 最终复验 7/7，Overview 6/6。
- 浏览器确认新版系统信息、七域统计与无 Recent logs；运行中的旧后端尚无新增字段且旧 `/api/health` 不可用，未擅自重启。窄屏/亮暗主题视觉验收及新版后端真实 S3/MQ 联调未完成；未执行远端写入测试。
