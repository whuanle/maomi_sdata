# iot — TDD

> 关联：[bdd](bdd.md) · [sdd](sdd.md) · [sop](sop.md)（四件套随 T142 收尾补齐）

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @IOT-S4 | `ui/src/api/__tests__/client.test.ts`、`MqttAuthModal.test.tsx` | 2026-11-19：按用户要求直接传播解析错误、移除认证弹窗说明；专项9/9通过。类型检查被无关ConsumptionStatus.tsx的notifications属性/未使用变量错误阻断。未重启后端。 |
| @IOT-S3 | DeviceCredentialCheckerTests.Broker_AuthModes_ApplyToNewConnections、SettingsTests.MqttAuth_*、MqttAuthModal.test.tsx | 2026-11-19：IoT30/30、Database19/19、Settings专项12/12、Store243/243，前端全量708/708、构建/typecheck/lint通过；末轮认证/设备权限专项16/16、持久化重新加载专项12/12通过。真实部署配置未改；浏览器已确认接入认证入口，点击超时，表单保存及demo联网未验收。 |
| @IOT-CRED1/CRED2/B4/B10 | `DeviceCredentialCheckerTests`、`DeviceCredentialTests` | 2026-11-20：IoT45/45、Database21/21；未知设备允许认证、禁用拒绝，删除后重新注册；旧哈希兼容。 |
| @IOT-S1/S2 | `DevicesPage.test.tsx`、`mqttSource.test.ts`、`ChartExplorer.test.tsx`、`DashboardLayoutValidationTests` | 2026-11-19：设备页9/9、图表专项46/46、Dashboard67/67、Store216/216；最终前端648/648，生产构建通过（包体大小警告）。浏览器已确认设备工具条布局；真机上报及浏览器保存重开完整链路未验收。 |
| @IOT-B1 | `dotnet test tests/SData.Iot.Tests` → MqttBrokerIntegrationTests.Publish_SingleValue_EnqueuedToBuffer | ✓ 2026-11-15（25/25） |
| @IOT-B2 | 同上 → Publish_BatchArray_EnqueuesAllRecords | ✓ 2026-11-15（25/25） |
| @IOT-B11 | 同上 → Publish_MultipleTopics_MixedForms_AllEnqueued（多级信号锁定斜杠形态 line1/pressure） | ✓ 2026-11-20（47/47） |
| @IOT-B12 | 同上 → Publish_SameSignalFromTwoDevices_IsolatedByDeviceKey（同名信号按 device_key 隔离） | ✓ 2026-11-20（47/47） |
| @IOT-B3 | 同上 → Publish_RetainedMessage_ReceivedByLaterSubscriber | ✓ 2026-11-15（25/25） |
| @IOT-B4 | `Broker_DisabledRejected_UnknownConnectDoesNotRegister`、`Broker_OnlineDeleteRegistersNewId_OnlineDisableBlocksAllSessions` | 2026-11-20：IoT45/45，禁用拒绝，删除不阻止重新注册。 |
| @IOT-B5 | 同上 → Publish_CrossDeviceTopic_RejectedAndNotEnqueued | ✓ 2026-11-15（25/25） |
| @IOT-B6 | 同上 → Publish_InvalidPayload_RejectedWithPayloadFormatInvalid | ✓ 2026-11-15（25/25） |
| @IOT-B7 | 同上 → Publish_BufferFull_RejectedWithQuotaExceeded | ✓ 2026-11-15（25/25） |
| @IOT-B8 | 同上 → Connect_NoCheckerRegistered_RejectedAsNotAuthorized | ✓ 2026-11-15（25/25） |
| @IOT-B9 | `MqttBrokerIntegrationTests.Start_RegardlessOfLegacyEnabled_BindsPortAndAcceptsConnections` | 2026-11-20：IoT33/33、Database21/21；缺失、false、true、非法旧启停值均监听配置端口并接受连接，停止宿主后释放监听状态。完整宿主构建被 `MqRuntime.cs:367` 的 `TopicEntry.Blocked` 缺失（CS1061）阻断，见 `.artifacts/mqtt-always-on-build.log`；未重启运行中的后端，部署联通未验收。 |
| @IOT-B10 | `DeviceCredentialCheckerTests`（DI、认证、自动注册、禁用和删除） | 2026-11-20：45/45；隔离宿主三台设备无需预注册，6条遥测确认并可查询。 |
| @IOT-S5/S6/S7 | `Broker_FirstValidPublish_RegistersOnceAndFlushesLastSeen`、`Broker_UnknownWrongKey_DoesNotRegister`、`Registry_ConcurrentFirstPush_OneRow_DeletedRegistersAgain_DisabledStaysBlocked` | 2026-11-20：IoT45/45，三认证模式、非法消息不注册、并发单行及LastSeen通过。 |
| @IOT-S8 | `Publish_RegistrationFailure_RetryWithoutEnqueueOrLastSeen` | 2026-11-20：IoT45/45，失败回执、重试及缓存通过。 |
| @IOT-S9/S10 | `Broker_OnlineDeleteRegistersNewId_OnlineDisableBlocksAllSessions`、`.artifacts/mqtt-autoreg-e2e.py` | 2026-11-20：在线禁用多连接拒绝，物理删除无旧行、重发新ID；隔离宿主验收通过，结果见 `.artifacts/mqtt-autoreg-e2e-20261120-122956/result.json`。 |
| @IOT-M1 | MQTTX 手工清单（configs/mqtt-example.md） | MQTTX/真机未执行；独立demo隔离宿主链路已验证。 |
| @IOT-S11 | `ui/src/pages/iot/__tests__/DevicesPage.test.tsx`（主题清单/完整主题串/状态量标注/行切换不重查/时间窗步长重查） | 2026-11-20：DevicesPage 13/13、前端全量 748/748、typecheck 通过。真机浏览器抽屉链路未验收。 |

验证环境备注（2026-11-15，worker-20261115-193608/T140）：全量 `dotnet build SData.slnx` 当时被 T610 在途 WIP（MainModule 引用未建好的 Rum 项目）阻塞，T140 范围链自证绿——`SData.Iot.Core` 构建 0 错、`tests/SData.Iot.Tests` 25/25 ✓、回归 Store.Tests 40/40 ✓、Ingest.Tests 17/17 ✓、Database.Tests 8/8 ✓；另在仓库外隔离工程（%TEMP%/t140-verify，仅编译 Mqtt 源码 + Store.Shared）验证 25/25 ✓。

自动注册收尾（2026-11-20）：`.artifacts/mqtt-autoreg-{iot,database,store,build}.log`，IoT45/45、Database21/21、Store认证及遥测专项6/6；完整宿主构建0错误（存在分析器警告）。demo构建0警告0错误、离线自检通过；隔离宿主三设备自动注册、查询与LastSeen、删除后新ID注册通过。未重启现有5300/1883宿主；当前部署需更新重启后生效。

本轮日志：`.artifacts/mqtt-ui-tests-final.log`、`mqtt-ui-build-final.log`、`mqtt-store-tests.log`。首轮前端647通过/1失败（Analytics schema展示），构建被GroupEditor在途改动阻断；未修改这些无关文件，最终重跑均通过。
