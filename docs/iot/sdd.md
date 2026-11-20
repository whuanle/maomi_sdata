# iot — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md) · 证据：`tests/SData.Iot.Tests`、`tests/SData.Database.Tests`

## 目标

内嵌 MQTT broker（本机单实例无集群）接收设备遥测，经内存缓冲零磁盘 IO 落 DuckDB；
支持免 key、统一 key、key 前缀三种接入认证；未知设备首次有效发布自动注册，设备元数据与遥测分层存储。

## 组件

- `MqttBrokerService`（`Iot.Core/Mqtt/`，BackgroundService）：MQTTnet 5.2 服务端；连接验证
  （username=DeviceKey / password 按认证模式填写）→ ACL（仅 `iot/{自身 deviceKey}/...`）→ 主题协议解析 →
  `IIngestBuffer.TryEnqueueTelemetry`；缓冲满/越权/非法消息体通过 PUBACK 原因码背压（QoS1）。
- `MqttTopicProtocol`：主题解析（`iot/{key}/{signal}`，信号支持多级）与消息体解析
  （单值 JSON 对象/数字/字符串、批量 JSON 数组；v|value、s|signal、ts|time、q|quality）。
- `MqttDeviceAuth`：`IDeviceCredentialChecker` 抽象 + `MqttPasswordHasher`（pbkdf2-sha256$iter$salt$hash）；
  broker 未注册校验器时 fail-closed 拒绝全部连接。
- `DeviceCredentialService`：共用合法标识规则；未知或已删除设备可通过认证，活跃禁用设备拒绝；不在 CONNECT 时注册。
- `MqttDeviceRegistry`：ACL 与完整消息体校验通过后，经独立 EF 作用域创建未知设备；默认名称=DeviceKey、启用、空分组和标签，雪花 ID 与自动审计。单例锁与唯一索引去重，唯一冲突重读赢家，其余存储失败拒绝发布。
- `DeviceLastSeenBuffer + DeviceLastSeenFlusher`（T141）：LastSeen 内存累积，30s 单事务批量 UPDATE。

## 数据布局

- DuckDB `iot_yyyymmdd`（窄表：device_key/signal/ts/num_value/str_value/quality），由 IngestWriterService 批量落库。
- SQLite `device`：`secret_hash`（PBKDF2 哈希）、`last_seen_time`（Flusher 批量回写）。

## 关键决策

1. **MQTTnet 5.2 拆包**：server 在独立包 `MQTTnet.Server 5.2.0.1603`（T000 封板遗漏），以
   `CentralPackageVersionOverrideEnabled` + VersionOverride 引用（不动封板的 Packages.props）；详见 STATE 决策日志。
2. **凭证抽象**：broker 只依赖 `IDeviceCredentialChecker`；真实校验由 T141 提供并注册（补注册见 STATE 冲突区），
   未注册时拒绝全部连接（fail-closed），设备校验器缺失时不发生数据放行。
3. **背压语义**：接入缓冲满 → PUBACK `QuotaExceeded`，设备端可重试；broker 不落盘、不阻塞其他设备。
4. **消息体一律合法 JSON**：裸字符串不是 JSON，按 `PayloadFormatInvalid` 拒绝（协议见 MqttTopicProtocol.cs 头注释）。
5. **LastSeen 批量**：每消息仅内存累积 O(1)，Flusher 30s 单事务批量回写，逐消息零 SQLite 写。
6. **MQTT 始终开启**：broker 随宿主启动，无配置启停开关；默认监听 1883，仅保留 `SData:Mqtt:Port`。遗留 `Enabled` 值一律忽略，认证模式与设备启用校验不变。
7. **主题与信号多对多**：主题不预注册、按条解析——一个设备可发布自身子树下任意多个主题与信号形态；
   不同设备发布同名信号互不冲突，属性序列唯一身份为窄表 `device_key + signal` 两列组合，查询按 device_key 隔离。
   ACL 将设备标识固定在主题第 2 段，故两台设备不可能发布同一条完整主题；「固定公共主题 + 载荷带设备 id」的
   固件模式按 `NotAuthorized` 拒绝，由网关侧改造（每设备子主题 / 多级信号 / 批量拆分）后接入。

## 已知问题

- 免密码无法防止设备标识冒用，仅限可信内网；禁用不主动踢线，但在线连接后续发布被拒。发布主题有限制，但未实现订阅隔离。
- MQTT（设备遥测）是逻辑数据源，内部 source=`iot`、SQL=`{{iot}}`；包含 MQTT 与 HTTP 写入的全部遥测，不新增物理 mqtt 表。设备台账仍在 SQLite，遥测仍在 DuckDB 按天表。
- 图表探索与仪表盘支持数值聚合、事件表及字符串模式；热力图和统一搜索不扩大来源范围。
- 首次合法发布先保存设备元数据再入遥测缓冲；若缓冲满，保留注册并返回 QuotaExceeded。部分容量不足的批量发布可能部分入缓冲，重试可能重复。
- 连接按全局状态版本缓存校验结果，普通发布不访问 SQLite；保存/禁用/删除后失效，所有在线连接下一条消息重检。禁用拒绝，删除后重建新 ID，不保留逐设备墓碑。
- 有效遥测全部进入缓冲后，以服务端接收时间 Touch，30s 批量更新 LastSeen；不使用设备自带时间判断活跃。
- MQTTX/真机 e2e 待集成窗（@IOT-M1）。

## 接入认证

- 管理员在设备页“接入认证”设置 none/shared/prefix；username=DeviceKey，password=接入key，与HTTP系统key无关。前缀字面区分大小写，禁止空前缀。
- SQLite setting 的 system:mqtt-auth 保存模式、PBKDF2哈希和前缀长度，不保存明文；响应仅模式与HasKey。不存在配置默认none，加载失败或损坏配置拒绝连接。
- 同模式key留空保留，切换认证模式需新值，none清除材料。保存后新连接生效，已有连接需重连；不提供主动踢线或订阅隔离。
- 模拟器只在独立demo项目；固定地址、三设备、频率和演示key，双击start-mqtt.cmd启动，主项目不包含模拟功能。
