# ingest — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md)

## 接入配置

| 协议 | 端点 | 认证 |
|---|---|---|
| OTLP/HTTP（标准+旧路径） | `POST /v1/{logs,traces,metrics}` 与 `/api/ingest/otlp/v1/*` | 三态：`X-Api-Key` / `Bearer` / `Basic`(password=Key) |
| OTLP gRPC | gRPC 服务（经 `SData:Listeners` 4317 grpc 端口或主端口 h2c） | 同上（metadata 头） |
| ES Bulk | `POST /_bulk` | 同上（Filebeat Basic） |
| 简单 JSON 批量 | `POST /api/ingest/{logs,traces,metrics,iot}` | 同上 |
| Kafka 日志消费 | 配置驱动：多实例（日志消费页 `system:mq` 或文件 `SData:Mq:Instances`；旧三键 `SData:Kafka` 兼容合成），每实例 `{BootstrapServers,GroupId,Topics:[{Name,Format,Parser}]}` | `SecurityProtocol/SaslMechanism/SaslUsername/SaslPassword`；生产优先 SaslSsl |
| RabbitMQ 日志消费 | 同上，实例 `{Uri,Topics:[…]}`；prefix 需管理 API 和 vhost 权限 | AMQP Uri 内置凭据 |
| Pulsar 日志消费 | 同上，实例 `{ServiceUrl,SubscriptionName,Topics:[…]}` | broker 侧直连（token 暂未暴露） |

- OTel Collector 对接清单见 [configs/otel-collector-example.yaml](../../configs/otel-collector-example.yaml)；Filebeat 见 configs/filebeat-example.yml。
- Key 在系统设置 API & Agents 管理，管理员可显式查看 Key（旧哈希记录需先重置）；重置、停用、删除对后续请求立即生效，查看与密钥环备份要求见 [设置运维](../settings/sop.md)。服务器 HTTP/gRPC 强制鉴权，RequireAuth=false 不再生效。升级会禁用已知默认开发 Key；已有自定义 Key 保留。InitialApiKey 仅显式首次初始化，撤销后重启不会恢复。生产使用 TLS；浏览器 RUM 不携带此秘密。
- MQ 多实例（日志消费页 `/log-consumption`，保存后 10s 内自动热更新，无需重启）：每实例独立连接与 topic 列表；每 topic 解析规则 8 种——`otlp-proto`（collector Kafka exporter 默认）/ `otlp-json` / `json`（兼容 /api/ingest/logs 形状或配字段映射）/ `regex`（命名捕获组，如 log4j/nginx 行）/ `csv`（分隔符+列索引）/ `logfmt`（key=value）/ `syslog`（RFC5424/RFC3164）/ `raw`（纯文本行）。解析规则可在页面「测试解析」粘样例试跑（不落库）。
- 消费异常默认按5/10/20/40/60秒退避，封顶后固定60秒重试；初始/最大间隔可在实例编辑中调整。Status 显示下次重试和倒计时；立即重试会重建整个实例，需确认，不会重放已确认消息。坏格式保持原消息阻塞重试，不确认、不推进；修复规则保存后热更新并重新处理未确认消息。
- 消费通知默认关闭，选择系统已启用渠道后才发送；默认每队列/渠道十分钟最多一次，窗口内合并计数。发送失败也占窗口，下一次新失败可携带上个窗口汇总；冷却记录存SQLite，重启不清零。恢复通知本期不发送。Status 可查看投递结果、冷却截止及抑制次数。
- Status 按实例和配置主题汇总，前缀不展开实际队列；最近 60 秒速度不是 broker lag。解析失败与确认失败独立显示，落库成功不代表确认成功；失败不以原文落库替代消费成功。
- Status 展开「最近失败」核对阶段、失败时间和安全原因；同时核对阻塞消息数、阻塞时长和下次重试时间。等待超过 30 秒仅提示排查，不断言已失败；共享 writer 失败摘要用于定位下游重试。
- 解析失败、空载荷、零记录及部分失败均不确认、不写入保底记录，保持阻塞直到原消息成功。使用「测试解析」排查规则并修正保存；持续无效的消息将持续阻塞。当前Kafka按实例串行消费，RabbitMQ共享通道也可能影响同实例其他队列；Pulsar按实际主题循环。broker保留期/TTL仍由部署保证。

## 常见问题

| 现象 | 处置 |
|---|---|
| 401 | 缺失、错误、停用或已撤销的接入 Key；服务器推送始终要求凭据 |
| 429 partialSuccess | 缓冲满：调大 `Ingest:BufferCapacity`，检查落库是否受阻（看 writer 日志） |
| OTLP 请求 415/400 | 确认协议为 http/json（protobuf 需另行扩展） |
| 数据延迟可见 | 缓冲批量 ~200ms 落库属正常 |
| MQ 消费未启动 | 实例 `Enabled=true` 且连接串与 Topics/Queues 非空；管理器日志有「MQ 消费者已启动/已停止」记录；实例被过滤（禁用/缺连接/无有效 topic）则不出现在期望集 |
| MQ 配置改了没生效 | 多实例热更新检测周期 10s；确认保存成功（PUT /settings/mq-instances 200）且实例指纹变化（改的是已启用实例）；旧单实例键在保存过 system:mq 后不再读取 |
| 解析结果字段不对 | 日志消费页该 topic「测试解析」试跑样例；检查字段映射/时间格式/级别别名；失败行按 `_parse_error` 标记过滤 |
| MQ 消费暂停/滞后 | 缓冲满退避重试（日志「接入缓冲已满」）：调大 `Ingest:BufferCapacity` 或排查落库阻塞；恢复后自动续消费不丢（at-least-once） |

## 验收流程

- 外部 OTLP 401/403 时检查对应信号 Header、协议和接收端权限；不要关闭本地采集。Header 配置、保留与清空见 [settings 运维](../settings/sop.md)，不会转发给内部接收器。

- 自身链路和指标固定写入本机，不配置外部 OTLP 也会采集。检查 service=sdata，等待链路批次约 5 秒、指标约 60 秒及缓冲落库；外部地址清空仅停外部出口。容量满时仍按缓冲策略丢弃，不承诺零丢失。专项验证见 `SelfTelemetryTests`。

1. `dotnet test tests/SData.Ingest.Tests` 全绿。
2. curl 接入样例 → `/api/logs` 可查询。
3. MQ 真实环境、限速造数、主题规则、停启命令和验收脚本见 [MqProducer](../../tool/MqProducer/README.md)。前缀匹配区分大小写；Pulsar 使用完整主题名前缀。RabbitMQ 消费已有队列，不自动声明（避免与既有队列参数冲突）。
4. MQ 等待事务提交再确认，失败批次重试；取消时不确认，重投允许重复。持续落库失败会阻塞后续批次，先修复 writer 错误，不要通过扩大缓冲掩盖故障。
