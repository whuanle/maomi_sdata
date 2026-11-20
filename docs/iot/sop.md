# iot — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md)

## 接入配置

| 键 | 说明 |
|---|---|
| 启动行为 | 随 SData 始终启动，无启停开关；遗留 `SData:Mqtt:Enabled` 不再生效 |
| `SData:Mqtt:Port` | 监听端口（默认 1883） |
| 免密码边界 | 仅可信内网；设备标识不是凭据，无法防止冒用；不使用系统 key |
| 连接参数 | username=DeviceKey / password 按接入认证填写；完整清单见 [configs/mqtt-example.md](../../configs/mqtt-example.md) |

## 常见问题

| 现象 | 处置 |
|---|---|
| 连接被拒 BadUserNameOrPassword | DeviceKey 格式非法、禁用，或key不匹配/认证配置无效；未知合法设备无需预注册 |
| 连接被拒 NotAuthorized | 凭证校验器未就绪（看 SData 日志） |
| PUBACK NotAuthorized | 越权主题：必须 `iot/{自身 DeviceKey}/` 开头 |
| PUBACK PayloadFormatInvalid | 消息体非合法 JSON 或缺 v/value、批量缺 s/signal |
| PUBACK QuotaExceeded | 缓冲满：稍后重发；调大 `SData:Ingest:BufferCapacity` |
| LastSeen 不更新 | 成功接收后约30秒批量回写，检查回写日志；设备自带时间不影响活跃时间 |
| PUBACK ImplementationSpecificError | 首次注册存储失败，检查 SQLite 日志后重试；不自动降级放行 |
| broker 不监听 | 内嵌服务始终启动，检查后端启动异常及端口占用；端口冲突时修改 `SData:Mqtt:Port` 并重启 |

## 验收流程

1. `dotnet test tests/SData.Iot.Tests`（broker/协议/自动注册/删除/ACL/背压）与
   `dotnet test tests/SData.Database.Tests`（凭证/LastSeen）全绿。
2. 启动 SData（MQTT 自动监听默认或配置端口），MQTTX 直推样例消息 → 「IoT 数据」页面可查询（e2e 待集成窗）。
3. 每次代码变更后重跑上述两个测试项目，并对照 docs/iot/tdd.md 回填证据日期。

## 数据源与工具条

- 设备页操作顺序为搜索、查询、新建；新建仅写角色可见，查询及回车回到第一页。
- 图表探索选择 MQTT（设备遥测），数值字段为 num_value，按 device_key/signal 分组；事件表可查看 str_value。
- 逻辑源包含 HTTP 与 MQTT 遥测，内部占位符 {{iot}}；SQLite device 保存设备台账，DuckDB iot_yyyymmdd 保存遥测，无需迁移。
- 禁用后所有在线连接的后续发布也拒绝；不主动踢线，不提供订阅隔离。删除物理移除台账，下次合法上报重新注册新 ID；历史遥测不删除。管理 API 和 HTTP ingest 鉴权保持独立。

## 三模式认证

管理员在设备页打开“接入认证”：免key忽略password，统一key完全相等，前缀模式按字面区分大小写。非空key最多256字符，前缀不能空白；同模式留空保留已有值，切换模式重新填写，免key清除旧材料。保存后设备需重连；三种模式都允许合法新设备首次有效上报自动注册，禁用设备不能通过上报恢复；删除不保留台账，下次有效上报作为新设备注册。
独立demo双击 F:/demo/sdata-demo-client-mq/start-mqtt.cmd；统一key用演示值 sdata-demo-key，前缀用 sdata-demo-，免key忽略。默认本机1883及demo-device-01/02/03，无需预注册；按服务端策略配置 mqtt.password 或 SDATA_MQTT_KEY。不要把演示key用于生产。

- 接入认证出现非JSON响应：先检查 `/api/settings/mqtt-auth` 响应类型；旧宿主缺路由可能返回SPA网页。更新并重启后端，新宿主未知API返回JSON 404。页面直接展示实际请求或解析错误，不替换为版本诊断建议；重启会断开已有MQTT连接，需安排后再操作。
