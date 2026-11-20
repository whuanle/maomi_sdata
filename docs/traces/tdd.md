# traces — TDD（验证映射）

> 关联：[bdd](bdd.md) · [sdd](sdd.md) · [sop](sop.md) · 上游：[store](../store/tdd.md) · 证据：[侧栏测试](../../ui/src/pages/search/__tests__/TraceDetailDrawer.test.tsx) · [算法测试](../../ui/src/pages/search/__tests__/traceTimeline.test.ts)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @TRACES-SEARCH1 筛选与聚合 | `tests/SData.Store.Tests/QueryDslTests.cs`、`QueryDslExecutorTests.cs` | 历史通过（2026-11-15），本次未重跑此组 |
| @TRACES-SEARCH2 跨天 | 同上，跨天天表用例 | 历史通过（2026-11-15） |
| @TRACES-SEARCH3 非法条件 | `QueryDslTests.cs` | 历史通过（2026-11-15） |
| @TRACES-UI-DEEPLINK 日志定位链路 | `SearchPage.test.tsx` 深链用例 | 通过（2026-11-17） |
| @TR-S1 专属筛选 | `SearchSidePanel.test.tsx`、`traceTimeline.test.ts`（根过滤与 URL 恢复） | 自动通过；浏览器确认专属面板；完整手动回归待补（2026-11-17） |
| @TR-S2 完整行展开 | `traceSearch.test.ts` 验证 trace/span 身份参数绑定、完整属性补查与缺身份保护；浏览器手动步骤见 SOP | 自动通过；完整手动验收待补（2026-11-17） |
| @TR-S3 时间轴属性联动 | `TraceDetailDrawer.test.tsx`（选择、折叠、复位）、`traceTimeline.test.ts`（微秒/异常树） | 自动通过；暗色实际数据侧栏布局已查看，亮色/窄屏完整回归待补（2026-11-17） |
| @TR-S4 有限数据与重试 | `TraceDetailDrawer.test.tsx` 失败重试/截断提示，`traceTimeline.test.ts` 缺父/循环保护 | 自动通过（2026-11-17） |
| @TR-S5 微秒与事件 | `OtlpParserTests.cs`、`OtlpProtoParserTests.cs` 新用例 | OTLP 定向 20/20 通过（2026-11-17） |

## 2026-11-18 Trace 与上下文改进

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @TR-S6 缩放与关联详情 | `TraceWaterfall.test.tsx`、`utils/__tests__/format.test.ts` | 自动测试通过；真实图表交互未完成验收（2026-11-18） |
| @TR-S7 周边链路与日志 | `surroundingContext.test.ts`、`SurroundingContextTab.test.tsx` | 微秒窗口、绑定、锚点不漂移、分页与重试通过；两域真实数据联动待验收（2026-11-18） |
| @TR-S9 两端识别片段 | `TraceWaterfall.test.tsx` renderItem 几何/标签/点击测试 | 相关 2 文件 9 用例、修改文件 ESLint、TypeScript 通过（2026-11-18）；实际 canvas 省略与窄屏待验收 |
| @TR-S8 大链路 | `TraceWaterfall.test.tsx` 一万片段可视行用例 | 有界绘制与未关联日志入口通过；真实浏览器性能待验收（2026-11-18） |

- 最终工作区类型检查通过，全量 UI 测试 45 文件、289 用例通过，相关修改文件 ESLint 通过；日志：`.artifacts/trace-validation/verified-tests.txt`。此前并行 ChartExplorer 修改中的菜单引用/测试断言失败已在最新工作区复跑通过；旧失败日志保留，不作为最终状态。存在 RUM act 与 Node localStorage 警告。
- 最终 Vite 生产构建输出到 `.artifacts/trace-ui-final-build` 隔离目录，通过；有 SignalR 注释和包体积警告，未覆盖宿主运行资源。
- 浏览器可进入搜索，但语义定位点击超时，节点点击数据源选择器未展开；未将该情况判为 Trace 验收通过。亮暗、窄屏、滑块拖动、实际日志/事件点击仍待验收；截图证据见本会话工具返回。
- 本轮未修改数据库结构、未重启服务、未提交或推送。

## 独立服务地图（2026-11-19）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @TR-S10 时间和条件 | `ui/src/pages/service-map/__tests__/serviceMap.test.ts`、`ServiceMapPage.test.tsx` | 参数绑定、窗口、页面冒烟通过；浏览器登录后待验收 |
| @TR-S11 整 Trace 采样 | `AnalyticsSearchSqlTests.ExecuteSearchAsync_ServiceMap_TraceSamplingAndIdentity`、`serviceMapFixture.test.ts` | 前后端同源 SQL：跨天、去重、边界、父子身份、稳定采样通过 |
| @TR-S12 真实顺序钻取 | `ServiceMapPage.test.tsx`、`serviceMap.test.ts` | 样例钻取、固定执行窗、旧响应隔离、重试及回环布局通过；真实 canvas 交互待验收 |
| @TR-S13 不完整提示 | `serviceMap.test.ts`、`ServiceMapPage.test.tsx` | 缺父、展示裁剪、元数据解码通过；真实大图待验收 |
| @TR-S14 紧凑工具栏 | `ServiceMapPage.test.tsx` 控件宽度与响应分组断言 | 页面 4 用例、typecheck、Lint 通过（2026-11-19）；登录后宽窄屏视觉待验收 |
| @TR-S15 剩余高度 | `ServiceMapPage.test.tsx` 满高工作区、弹性画布与独立滚动断言 | 服务地图 14 用例、typecheck、Lint 通过（2026-11-19）；实际窗口缩放视觉待验收 |

- 最新完整 UI 回归 79 文件、555 用例通过，服务地图及菜单定向 14 用例通过，typecheck 与 ESLint 通过；日志 `.artifacts/service-map-ui-final.log`、`service-map-typecheck.log`、`service-map-ui-lint.log`。此前 Heatmap 失败已在最新工作区消失，不作为最终状态。
- Store 定向回归（AnalyticsSearchSqlTests / QueryDslTests / QueryDslExecutorTests / StoreIntegrationTests）78/78 通过；日志 `.artifacts/service-map-store-regression.log`。此前 RUM 编译阻塞后已恢复；未改动该阻塞相关代码。
- 浏览器打开 `/service-map` 后停在登录页，未使用凭证、未绕过认证；亮暗主题、真实图表缩放/时间桶/节点边点击、readonly 实际登录和冷热服务关系全链路尚未手动验收。
- 隔离 Vite 构建通过（`.artifacts/service-map-ui-build`，日志 `service-map-ui-build.log`），有既有包体积警告；首次命令未指定前端根目录失败，指定正确入口后通过。
- 不新增表、不重启服务，不覆盖宿主 wwwroot，不提交或推送。所有数字均为该次工作区快照验证结果。

## 先前验证（2026-11-17，非本轮结果）

- `npm --prefix ui run typecheck`、`lint`：通过。
- `npm --prefix ui run test`：31 文件、188 用例通过；存在其他 LiveView 测试的 act 警告。
- `npm --prefix ui run build`：通过，有既有 bundle 大小提示。
- OTLP：`dotnet test tests/SData.Ingest.Tests/SData.Ingest.Tests.csproj --artifacts-path F:/workspace/maomi_sdata/.artifacts/traces-validation -p:RunAnalyzers=false --filter "FullyQualifiedName~Otlp" --verbosity quiet`：20/20。
- 默认后端测试构建曾因运行中的 SData 锁文件失败；改用隔离产物目录，不停止服务。首次隔离测试还遇到同时进行的 MQ 接口变更编译错误，后续重跑已通过。
- 本地浏览器后续跳回登录页，亮色、窄屏和完整交互手动验收未完成；暗色实际数据侧栏已查看。
- 未执行生产服务重启、冷层实际数据全链路验收或深度安全扫描；未提交或推送 Git。
