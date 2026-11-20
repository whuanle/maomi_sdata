# MQTTX / 通用 MQTT 客户端直推 SData 清单（T142）

> 前置：启动 SData 后内嵌 broker 自动开启（默认端口 1883，无启停开关）；合法新设备通过认证后首次有效发布自动注册（无需预注册，见 docs/iot/sdd.md）。仅用于可信内网，设备标识无法防止冒用，不要向公网开放。

## 1. 连接配置（MQTTX）

| 项 | 值 |
|---|---|
| Name | sdata-iot-demo |
| Client ID | 任意（如 mqttx-demo） |
| Host | mqtt://127.0.0.1 |
| Port | 1883（`SData:Mqtt:Port`） |
| Username | 设备 DeviceKey（如 `dev-001`） |
| Password | 按设备页管理员接入认证：免key留空、统一key完整值、前缀模式填写以该前缀开头的key；不用HTTP系统key |
| Protocol | MQTT 5.0（推荐，PUBACK 能拿到拒绝原因码）；3.1.1 也可 |

- 连接被拒 `BadUserNameOrPassword`：DeviceKey 格式非法 / 已禁用 / 接入 Key 不匹配或配置无效。未知设备不是拒绝原因。
- 连接被拒 `NotAuthorized`：broker 已启用但设备凭证校验器未就绪（服务端问题，看 SData 日志）。

设备标识为1–64位 ASCII 字母、数字或 `_ - : .`。仅 CONNECT 不创建台账；首次通过自身主题与完整消息体校验后自动创建，默认名称为标识、启用、空分组和标签。已有设备信息不覆盖；禁用设备拒绝上报，删除不留台账，再次有效上报以新 ID 注册默认资料。注册后缓冲满仍保留台账，遥测稍后重试。

## 2. 发布主题与消息体

主题约定（ACL：只能发布 `iot/{自身 DeviceKey}/...`）：

```
iot/dev-001/temperature          # 单值：消息体 JSON 对象 / JSON 数字 / JSON 字符串
iot/dev-001/line1/pressure      # 信号名支持多级（原样落库为 line1/pressure）
iot/dev-001/batch                # 批量：消息体 JSON 数组（元素自带 s/signal）
```

主题不预注册、按条解析：一个设备可发任意多个主题；不同设备发同名信号互不冲突，
数据按 device_key + signal 唯一区分。ACL 把设备标识固定在主题第 2 段，两台设备
不可能发布同一条完整主题——固定公共主题 + 载荷带设备 id 的固件模式会被
`NotAuthorized` 拒绝，需网关侧改为每设备一个子主题或拆成多级信号/批量消息。

消息体示例（单值）：

```json
{"v": 21.5, "ts": "2026-11-15T21:00:00+08:00", "q": 0}
```

```json
{"value": "RUN"}
```

```text
42
```

消息体示例（批量）：

```json
[{"s": "temperature", "v": 21.5}, {"signal": "state", "value": "RUN", "ts": 1741998600000, "q": 1}]
```

字段约定：`v|value` 必填（数值→数值序列、字符串→状态序列）；`s|signal` 批量必填；
`ts|time` 缺省取接收时间；`q|quality` 缺省 0（0=good）。**消息体一律合法 JSON**（裸字符串
不是 JSON，会收到 `PayloadFormatInvalid`）。

## 3. QoS 与 retain

- QoS 0/1 均可；QoS1 的 PUBACK 携带业务拒绝原因码：
  - `NotAuthorized`：越权主题（发布 `iot/{别的设备}/...`）
  - `PayloadFormatInvalid`：消息体不是合法 JSON / 字段缺失
  - `QuotaExceeded`：接入缓冲满（稍后重发）
- retain 支持：保留消息按主题缓存在 broker 内存，新订阅者立即收到最近一条。

## 4. 验证查询

数据经内存缓冲批量落库（默认 ~200ms 批），落库后即可在「IoT 数据」页面按设备/信号查询；
也可 `POST /api/iot/search`（查询 DSL，见 docs/store/sdd.md）。

## 5. 常见排错

| 现象 | 处置 |
|---|---|
| PUBACK `NotAuthorized`（已连接） | 主题必须以 `iot/{自身 DeviceKey}/` 开头 |
| PUBACK `PayloadFormatInvalid` | 检查消息体 JSON 合法性与必填字段（v/value、批量 s/signal） |
| PUBACK `QuotaExceeded` | 缓冲满：稍后重发；服务端调大 `SData:Ingest:BufferCapacity` |
| 连接被拒 | 见第 1 节拒绝码说明 |
| 数据查询不到 | 缓冲批量落库有 ~200ms 延迟；确认设备未被禁用（禁用设备连接被拒） |

## 6. 数据源和安全边界

在图表探索选择 MQTT（设备遥测）；内部仍使用 {{iot}} 和 iot_yyyymmdd，不创建物理 mqtt 表。此源包含 HTTP 接入遥测，设备元数据仍保存在 SQLite device。
禁用拒绝新连接及在线连接后续发布；删除后在线连接再次有效上报也重新注册。发布只能使用自身标识主题，但免密码不能证明设备身份，订阅不隔离。HTTP ingest 与管理 API 的鉴权不变。

接入认证保存后新连接生效；已有连接需重连。统一key与前缀只存哈希、不回显，同模式留空保留，切换模式需新值；空前缀拒绝。独立demo已固定sdata-demo-key，统一模式配置同值、前缀模式可用sdata-demo-，仅限演示内网。
