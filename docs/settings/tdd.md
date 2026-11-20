# settings — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @SET-S43/S44 查看及重置 Key | ApiAgentTests、ApiKeySchemaTests、ApiKeyManagementIntegrationTests、ApiAgentsPanel.test.tsx | 2026-11-20：Store252/252、补列2/2、隔离JWT权限/无缓存1/1、前端定向24/24、类型检查和独立构建通过；前端全量730/731（日志消费页旧文案断言失败），Ingest整组被MqRuntimeTests缺FallbackState编译阻塞。未部署、未重置真实Key、浏览器未复验 |
| @SET-S40 移除本机遥测提示块 | `SettingsPage.test.tsx`（提示块不存在、外部地址与 Header 保存保留） | 2026-11-20：设置页18/18、typecheck通过；仅移除展示，不改后端采集，未部署或浏览器复验 |
| @SET-S42 消费重试及通知渠道 | `SettingsTests.MqAlerts_ValidateEnabledChannelsAndPersistRetrySettings`、消费页保存测试 | 设置专项1/1、前端消费专项24/24通过（2026-11-20）；假通知，无真实外发 |
| @SET-S41 外部遥测 Header | `SettingsTests.OtlpHeaders_PreserveReplaceClearAndRedact`、`SelfTelemetryTests`、设置页 Header 测试 | 2026-11-20：设置13/13、遥测39/39、前端全量725/725、末轮Header页面专项2/2、typecheck/lint及diff检查通过；此前同期 ConsumptionStatus.tsx 未使用导入阻塞末轮已消失。未重启部署、浏览器或真实外部服务验收 |
| @SET-S38/S39 | ApiAgentTests、AgentProtocolTests、ApiAgentsPanel.test.tsx、SettingsPage.test.tsx | 2026-11-19：凭据生命周期/用途隔离/MCP协议通过；前端定向19/19，全量724/724；完整独立宿主401/403、审计和撤销验证通过；独立构建通过；类型检查曾通过，末轮被同期 ConsumptionStatus.tsx:11 未使用 notifications（TS6133）阻塞。浏览器登录点击超时，新面板视觉验收未完成；未部署到现有实例 |
| @SET-S40 固定本地与可选外部遥测 | `SelfTelemetryTests`；`SettingsPage.test.tsx` 本地只读/外部保存清空 | 2026-11-19：遥测专项 23/23（含真实 DuckDB 落库及本机 OTLP 双出口无递归）、宿主编译、typecheck/lint、遥测页面专项通过；此前设置相关 18/18，末轮前端全量 716/718，两项旧页签数量断言失败（预期6实际7）。未重启部署或浏览器验收 |
| @SET-S37 | DeviceCredentialCheckerTests.Broker_AuthModes_ApplyToNewConnections、SettingsTests.MqttAuth_*、MqttAuthModal.test.tsx | 2026-11-19：IoT30/30、Database19/19、Settings专项12/12、Store243/243，前端全量708/708、构建/typecheck/lint通过；末轮认证/设备权限专项16/16、持久化重新加载专项12/12通过。真实部署配置未改；浏览器已确认接入认证入口，点击超时，表单保存及demo联网未验收。 |
| @SET-S33/S34/S35/S36、S18/S20 按 S3 状态拆分入口及隐藏字段保真 | `SettingsPage.test.tsx`（开启/未配置、保存切换、草稿与测试不切换、失败重读、两页交替保存）；`api/__tests__/settings.test.ts` | 通过（2026-11-19）：定向 17/17、前端全量 717/717、typecheck、diff --check；未做浏览器复验、构建或真实 S3 操作 |
| @SET-S32/S29 移除摄取缓冲页签及旧链接回落、保留六页签 | `SettingsPage.test.tsx` + 通知渠道与概览测试 | 2026-11-19：首轮相关测试 25/25；末轮页签专项 6/6、typecheck/lint、diff --check 通过。全量 701/703，两处失败为日志消费展示及一天归档策略；首次类型错误在同期改动完成后消失；未做浏览器复验或构建 |
| @SET-S31 压缩卡片分层与响应式说明区 | `SettingsPage.test.tsx`（分区标题、逐项说明及保存）；typecheck/lint | 通过（2026-11-19）：设置页 16/16、typecheck/lint；浏览器受控页与用户页均为空，未做视觉复验或构建 |
| @SET-S31 冷归档压缩范围与编码说明 | `SettingsPage.test.tsx` 的 `saves compression codec`（说明展示及保存）；`npm --prefix ui run typecheck` | 通过（2026-11-19）：压缩用例 1/1、类型检查通过；设置页整组 10/11，摄取缓冲页签切换用例未找到卡片；浏览器未复验。首次运行另发现 Inbox 缺失导入，已补齐 |
| @SET-S30 | `AlertIntegrationTests.Save_RejectsVersionConflict_AndChannelSecretsNeverReturned`；[告警验证](../alerts/tdd.md) | 通过（2026-11-19），假传输，无真实外发 |
| @SET-S29 管理员页签不依赖设置加载成功 | `SettingsPage.test.tsx`（加载中保持七页签及深链、失败后页签保留/通知独立打开/重试恢复）；设置相关 16/16、前端全量 611/611、typecheck/lint/build | 通过（2026-11-19）；当前无可接管浏览器页面，未做真实页面复验 |
| @SET-S28/S2 移除重复服务状态、默认成员及旧链接回落 | `SettingsPage.test.tsx`（无状态展示/监控请求、默认成员、旧及非法链接回落、非管理员权限）；设置/通知/概览相关 20/20，前端全量 607/607、lint | 通过（2026-11-19）；typecheck 被未改动的 `SqlEditor.tsx:25` 未使用参数 `parameters`（TS6133）阻塞；浏览器未复验 |
| @SET-S27 移除顶部刷新与独立表格，实例按类型内嵌 | `SettingsPage.test.tsx` + `NotificationChannelsPanel.test.tsx`（紧凑列表分组/编辑/开关/无表格，列表不展示审计）；相关 12/12、typecheck/lint | 通过（2026-11-19）；浏览器外观未复验 |
| @SET-S26 十一种渠道多实例、同类型重名拒绝及独立更新 | `NotificationTests.Management_AllTypesSupportMultipleNamedInstances`（11 类型）；通知后端 37/37、前端设置相关 11/11、typecheck | 通过（2026-11-19）；仅本地测试，无真实外发 |
| @SET-S21/S22/S23/S24 通知配置、凭据、停用删除、草稿测试与目标限制 | `tests/SData.Store.Tests/NotificationTests.cs`（26 项，含 11 类型协议） | 通过（2026-11-19）；假传输，无真实外发 |
| @SET-S25 通知类型、保存不发送、凭据保留 | `NotificationChannelsPanel.test.tsx`；设置相关 12/12、前端全量 603/603、typecheck/lint、宿主编译 | 通过（2026-11-19）；浏览器停在管理员登录页，登录后联调与真实渠道投递未执行 |
| 通知表旧库增量修补幂等 | `tests/SData.Database.Tests/NotificationSchemaTests.cs` | 隔离项目 1/1 通过（2026-11-19）；原 Database.Tests 被既有 DeviceCredentialTests.cs:62 参数类型错误阻塞 |
| @SET-S18/S19 一天设置共享、请求契约、粒度保留 | `SettingsPage.test.tsx` + `api/__tests__/settings.test.ts`（7/7）；`SettingsTests`（含命令往返及 hour 保留） | 通过（2026-11-18）；Store 全量 146/146、前端构建通过。首次 typecheck 的图表 TS6133 在后续构建已不再出现；本次未修改图表。真实实例因未登录尚未保存一天策略 |
|---|---|---|
| provider 合并语义：文件回退 / 覆盖生效 / 脏 JSON 回退 / 压缩白名单回退 | `tests/SData.Store.Tests/SettingsTests.cs`（6 用例） | 通过（2026-11-16） |
| @SET-S3/S5 掩码语义 + 立即应用 SECRET | `SettingsTests.SaveS3_MaskedSecret_KeepsStoredSecret`、`SaveS3_MissingSecret_WhenEnabled_BadRequest` | 通过（2026-11-16） |
| @SET-S6/S7 运行时覆盖进入归档/保留 | `ArchiveTests.Archive_UsesRuntimeRetentionOverride`（文件 cold=0 → 覆盖后立即归档）、`Archive_UsesRuntimeCompressionOverride`（parquet_metadata 断言 ZSTD）、`Archive_UsesRuntimeS3OverrideForObjectKey` | 通过（2026-11-16） |
| 原归档/保留行为无回归（幂等/三段/旧形态） | `ArchiveTests` 原有 3 用例 + 全套 `dotnet test tests/SData.Store.Tests`（74/74） | 通过（2026-11-16） |
| @SET-S1 卡片渲染 + 掩码占位 | `ui/src/pages/settings/__tests__/SettingsPage.test.tsx`（5 用例：admin 卡片/非 admin 隐藏/S3 掩码保存/连接测试/压缩保存） | 通过（2026-11-16） |
| @SET-S2 非管理员整页不可管理（前端视角） | `SettingsPage.test.tsx hides settings cards for non-admin`（后端 403 由授权属性保证，待 e2e 固化） | 通过（2026-11-16，后端侧待手工复验） |
| @SET-S4 连接测试请求发出 | `SettingsPage.test.tsx tests s3 connection`（mock 断言请求载荷） | 通过（2026-11-16，真实 MinIO 连通待手工） |
| @SET-S8 MQ 实例保存热更新（管理器指纹 diff） | `MqConsumerManagerTests.ComputeDesired_*`（过滤/指纹稳定/白名单）+ 手工热更新观察（待执行） | 自动化部分通过（2026-11-17） |
| @SET-S9 参数校验 400 | FluentValidation 管线统一保证（管线已有 e2e），设置专属规则待手工复验 | 待验证 |
| @SET-S10 MQ 实例保存与校验（合法通过/非法拒绝） | `tests/SData.Ingest.Tests/SettingsMqValidationTests.cs`（2 用例：3 类型合法列表、重复名/未知类型/缺连接/bad regex/bad csv/bad format 全拒绝） | 通过（2026-11-17） |
| @SET-S12 解析预览（命中/失败标记） | `MqLogDecoderTests.Previewer_RegexSample_ReturnsRecordsOrError` | 通过（2026-11-17） |
| @SET-S13 MQ 凭据掩码往返 | `tests/SData.Ingest.Tests/MqSecretsTests.cs` + 真实 API 掩码 GET/PUT | 通过（2026-11-18，Ingest 69/69） |
| Status 替代全部页签及三协议详情回归 | `LogConsumptionPage.test.tsx` | 12/12 通过（2026-11-19）；浏览器未复验；全量及构建边界见 [Monitor 验证记录](../monitor/tdd.md) |
| @SET-S14/S15 类型页签、数量及新建默认类型 | `LogConsumptionPage.test.tsx` 页签筛选/停用计数/零实例/新建类型；消费页 7/7、前端全量 287/287、tsc、Vite build | 通过（2026-11-18） |
| @SET-S16/S17 分区编辑、配置摘要、取消与保存保真、解析预览 | `LogConsumptionPage.test.tsx`（10/10）、前端全量 298/298；浏览器已检查亮色列表和编辑侧栏 | 自动测试通过（2026-11-18）；浏览器确认亮暗列表、亮色侧栏、600px 单列和 Esc 关闭，未对真实实例执行保存/删除 |
| 前端全量检查 | `npm run lint` / `npm run build` / `npm run typecheck` | 未通过（2026-11-18）：首次 lint 缺少图表页引用的 ESLint 规则、build 报 formula.ts Object.hasOwn；末次 typecheck 报 ChartExplorerPage.tsx 未使用导入/状态及按钮事件类型错误。工作区其他文件持续变化，本次未修改图表代码 |
| 日志消费页多实例 UI（卡片渲染/开关保存/抽屉新建/非管理员） | `ui/src/pages/log-consumption/__tests__/LogConsumptionPage.test.tsx`（4 用例）+ vitest 全量 66/66 + tsc + vite build | 通过（2026-11-17） |
| 四类归档、迟到补报、失败重试与重启恢复 | `ArchiveLifecycleTests.cs` + Store 全量；设置页/API 测试 | 2026-11-19：Store 153/153、Metrics 46/46，前端相关 7/7，前端构建通过；未重启部署新后端，真实 S3 四类验收待部署 |
