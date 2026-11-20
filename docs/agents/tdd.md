# agents — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md) · [设置验证](../settings/tdd.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @AGENT-S4 | ApiAgentTests、ApiKeyManagementIntegrationTests；[设置验证](../settings/tdd.md) | 2026-11-20：创建/重置/再次查看及密文绑定通过；Store252/252、旧库补列2/2、隔离权限/无缓存1/1、前端定向24/24；未改真实Key，浏览器未复验 |
| @AGENT-S1/S2 | AgentProtocolTests；ApiAgentTests | 2026-11-19：真实 HTTP initialize/tools/list/tools/call、用途隔离和停用后401通过 |
| @AGENT-S3 | StoreIntegrationTests.AgentTools_QueryActualData_AndAnalyticsCancellationInterruptsNativeWork | 2026-11-19：真实日志/链路/指标查询和原生取消通过 |
| 后端回归 | tests/SData.Store.Tests；tests/SData.Ingest.Tests | 2026-11-19：Store247/247、Ingest99/99；此前 MQ 同期编译阻塞及新增验证接口命名错误已消除 |
| 前端回归 | ApiAgentsPanel.test.tsx；SettingsPage.test.tsx；全量 vitest | 2026-11-19：定向19/19、全量724/724；独立前端构建通过；页面验收继续记录 |
| 增量安全补测 | ApiKeySchemaTests；AgentProtocolTests.Budget_RejectsConcurrentAndExcessRequests | 2026-11-19：旧库增量补表/列幂等、撤销后重启不恢复2/2；最终协议/来源/限流/查询定向5/5 |
| 完整宿主 | `.artifacts/verify-api-agents.py`，独立5539端口与临时数据目录 | 2026-11-19：管理员401/403、审计、凭据用途隔离、写入、MCP初始化和即时撤销通过；不影响运行实例 |
| 构建与页面验收 | 独立宿主构建、前端构建、typecheck；浏览器 | 2026-11-19：宿主/前端独立构建通过；类型检查曾通过，末轮被同期 ConsumptionStatus.tsx:11 未使用 notifications（TS6133）阻塞；浏览器登录点击超时，已查看登录截图，新面板视觉验收未完成；无控制台采集能力 |
| @AGENT-S5/S6/S7 | `AgentAnalysisIntegrationTests.cs`：真实临时DuckDB目录/聚合/对比/标签/IoT/RUM/依赖去重/非法字段/预算；[执行日志](../../.artifacts/mcp-final-targeted.log) | 2026-11-20：定向10/10（含原鉴权回归）通过；早轮协议夹具缺服务注册、SSE解析不兼容已修复并复跑 |
| @AGENT-S8 | `AgentAnalysis_QueriesColdFamiliesAcrossUtcMidnight` | 2026-11-20：logs/traces/metrics/iot/sessions/rum_events 六类本地冷热跨UTC午夜合并通过；热表目录不列冷分区；真实S3未运行 |
| @AGENT-S9 | `AgentProtocolTests`、`ApiAgentsPanel.test.tsx` | 2026-11-20：26工具及只读标记/输入Schema、工具调用通过；界面分组专项7/7、前端全量732/732、前端构建通过；未做浏览器视觉验收 |
| 扩展最终回归 | [Store日志](../../.artifacts/mcp-store-final.log)、[UI日志](../../.artifacts/mcp-ui-full.log)；`AgentAnalysis_RealHttpMcpCallsUseDuckDbAndRejectUnsafeRequests` | 2026-11-20：Store256/256；真实HTTP初始化→发现→DuckDB明细/聚合/目录和非法请求拒绝通过；不使用运行实例或真实Key |
| 本次宿主构建 | [构建日志](../../.artifacts/mcp-host-build.log) | 2026-11-20：失败，IoT `MqttDeviceRegistry.cs:95` CS0234（Microsoft.Data引用缺失）；MCP模块构建通过。未修复无关模块，未部署或重启运行实例 |
| 未覆盖边界 | 生产反向代理、实际 Agent 产品、并发压测 | 待验收；自动测试与独立实例不等同于生产部署 |
