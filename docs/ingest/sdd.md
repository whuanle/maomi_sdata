# ingest — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md) · 证据：`tests/SData.Ingest.Tests`

## 目标

低摩擦数据接入：OpenTelemetry 生态（OTLP/HTTP 双格式 + OTLP gRPC）、ES Bulk 写入兼容、简单 JSON 批量与内嵌协议，经内存缓冲进入 DuckDB，接入路径零磁盘 IO、满负荷可降级背压。

## 组件

- `IngestController`（`/api/ingest/*`）：OTLP logs/traces/metrics + 简单 JSON 批量（logs/traces/metrics/iot）。
- `OtlpV1Controller`（`/v1/logs|traces|metrics`，T111）：标准 OTLP/HTTP 接收器，`application/x-protobuf` 与 JSON 双格式、gzip 请求体自动解压；旧路径 `/ingest/otlp/v1/*` 转发同一处理链（保留一个版本周期后废弃）。
- `OtlpGrpcLogs/Traces/MetricsService`（`Ingest.Core/Grpc/`，T112）：OTLP gRPC 三服务，复用 OtlpProtoParser 同一解析链与逐条入缓冲；缓冲满经 `partialSuccess` 上报拒绝数（Collector 可识别）。
- `OtlpLogParser / OtlpTraceParser / OtlpMetricParser`（JSON）+ `OtlpProtoParser`（proto，static，经 `OtlpProtoParserService` 包装注入）：resource 属性提取 `service.name`、severityNumber→文本、nanos 时间戳、kvlist→JSON 字符串、histogram 派生 `.count/.sum`。
- `EsBulkController` + `EsBulkParser`（`/_bulk`，T120）：NDJSON index/create → 日志（`_index`→logs.source），update/delete 返回 ES 形状错误项，缓冲满整批 429。
- `IngestApiKeyFilter`（T113 收口）：接入 key 三态提取——`X-Api-Key` 头 > `Authorization: Bearer` > `Basic`（username 任意，password=Key，Filebeat/Collector 惯例）；服务器写入强制鉴权，旧 `Ingest.RequireAuth=false` 忽略并警告；key 哈希存 SQLite `ingest_key`，有界缓存通过撤销版本立即失效。gRPC 使用 `IngestAuthenticationInterceptor`，标准 `/v1/*` 与 `/api/v1/*` 别名并存。默认开发 Key 被禁用，显式初始化 Key 不在删除后重播种。
- 宿主 `ConfigureGrpcModule`（T112）：AddGrpc + gRPC 服务注册；C4 `SData:Listeners:[{Port,Protocol:http|grpc}]` 通用附加端口（4317/4318/9200 全部由此实现；grpc=纯 HTTP/2，http=HTTP/1+2），主端口一并显式 Listen（Kestrel 显式 Listen 时 UseUrls 失效）。
- `Consumers/`（MQ 日志消费，多实例热更新，默认关闭）：`MqConsumerManagerService`（BackgroundService，每 10s 读 `GetMqInstances()`，按实例配置指纹 diff 差异化启停消费者——新增/修改/删除/停用实例自动重建对应消费者，无需重启宿主）+ 三个实例 Runner：`KafkaInstanceRunner`（Confluent.Kafka，手动批量提交 offset）、`RabbitMqInstanceRunner`（RabbitMQ.Client 7.x，手动 Ack + 自动断线恢复）、`PulsarInstanceRunner`（DotPulsar，逐条 Acknowledge）；`MqLogDecoder` 按 topic 配置的解析规则（8 种白名单，见下）分派 → `IIngestBuffer`；`MqLogPreviewer`（实现 Settings.Shared `IMqLogPreviewer`）供设置页样例试解析；本期仅日志，traces/metrics 留扩展点。
- **解析规则体系**（`MqLogFormat` 白名单，Infra.Configuration.Shared）：`otlp-proto`/`otlp-json`（OTLP 结构）、`json`（兼容 `/api/ingest/logs` 形状，或按 `MqParserOptions` 字段路径映射提取，嵌套点路径、剩余字段进 attributes）、`regex`（命名捕获组映射字段，每行匹配，未匹配续行并入上一条正文兼容堆栈）、`csv`（分隔符分列，映射字段填列索引）、`logfmt`（key=value）、`syslog`（RFC5424/RFC3164 自动识别，默认 tag→service、PRI%8→severity、msg→body）、`raw`（整条正文 + RFC3339 前缀嗅探）。公共映射层 `TextLogMapping`：时间（自定义格式串/自动嗅探/epoch，失败回退消费时间）、级别别名归一（warning→WARN、crit→ERROR、数值 0-7 syslog 表等，自定义 SeverityMap 优先）、`KeepUnmapped` 未映射字段进 attributes。

## 关键决策

- 外部 exporter 分别使用 TraceHeaders/MetricsHeaders，统一适用于 grpc/http-protobuf；显式空字符串覆盖 SDK 环境变量 Header，避免清空后继承其它凭据。Header 的掩码与保存规则见 [settings 设计](../settings/sdd.md)。鉴权失败不影响固定本地出口。

- 自身 Trace/Metrics 固定由宿主本地 exporter 写入 `IIngestBuffer`，不走 HTTP/gRPC、无需接入凭据。外部 OTLP 仅额外导出；导出期间抑制插桩并排除 OTLP 接收路径的服务器链路，防止回环递归。指标标签平铺并带 service/instance，直方图按现有存储保存 count/sum；满载沿用缓冲丢弃计数。

1. OTLP 标准路径 `/v1/*` 与 gRPC 双通道，旧 `/api/ingest/otlp/v1/*` 保留一个版本周期（迁移期并存，见 bdd @INGEST-OT4）。
2. 分层纯化：Api 只依赖 Shared 接口（`IIngestIngestor/IIngestKeyValidator/IOtlpParser/IOtlpProtoParser/IEsBulkParser`），实现在 Core；gRPC 服务类落 Core（proto 生成物在 Core，Api→Shared 单向无法引用），命名空间 `SData.Ingest.OtlpGrpc`（避开全局 `Grpc.*` 遮蔽）。
3. 缓冲满：HTTP 返回 429 + partialSuccess 提示、gRPC 返回 partialSuccess 计数，不阻塞、不落盘。
4. proto 选型：官方 opentelemetry-proto v1.3.2 + Grpc.Tools 编译期生成（`GrpcServices="Both"`），不引第三方 proto NuGet；生成物不提交。
5. Kestrel 明文端点 `Http1AndHttp2` 实际只服务 HTTP/1（h2c 需显式 `HttpProtocols.Http2`）——附加 grpc 端口必须纯 HTTP/2（集成测试实证）。
6. OTLP v1.3.2 形状：`ExportXxxPartialSuccess` 为顶级类型；proto 子消息默认 null 须显式 new。
7. MQ 消费定位为**可选输入通道**（默认关闭，不建任何连接，零外部依赖承诺不变）：MQ 不做内部中间层（内部缓冲仍由 IIngestBuffer 承担），只做外部日志管道的接入面。投递语义 at-least-once：缓冲满退避重试且不确认/不提交 offset，broker 不可达按 5/10/20/40/60 秒封顶退避，之后固定 60 秒重试，不崩溃宿主。Kafka 支持 SASL（Plain/SCRAM）及 TLS 协议选择；RabbitMQ 凭据经 AMQP URI，Pulsar 管理发现支持 Bearer token（数据通道 token 尚未暴露）。MQ 等待批量事务提交后确认，失败批次保留重试；故障重投可能重复，不承诺 exactly-once。
8. **多实例热更新（2026-11-17）**：配置从「每类 MQ 单实例三键」升级为 `system:mq` 实例列表（Settings 域），管理器按实例指纹（配置 JSON）diff，只重启受影响实例；旧三键未保存新配置时内存合成兼容（见 settings 模块）。实例粒度：每实例独立连接 + 自己的 topic/队列与解析规则。
9. **失败不跳过**：解析失败、空载荷、未知格式、零记录或部分记录失败均阻塞整条原消息，不写保底记录、不确认、不推进后续消费。共享处理循环持有原载荷退避重试；只有完整解析并提交后才返回给协议确认。解码器产生的失败记录仅用于预览/诊断，不能作为消费成功依据。

10. **主题规则（2026-11-18）**：`MatchMode=exact/prefix`（旧配置默认 exact），精确优先、其次最长前缀，同模式同名拒绝。Kafka 转义前缀后订阅正则并周期刷新元数据；RabbitMQ 经管理 API 按 vhost 发现队列名；Pulsar 经管理 API 按 tenant/namespace 发现完整主题名，运行中新主题自动纳入。实际主题写 source，实例/规则/主题写 `_mq_*` 属性。
11. 运行态经 Monitor 返回，不含凭据；消费会话以实例 ID、配置主题规则和会话 ID 隔离，60 秒固定窗口分别统计消息/记录/字节速率，解析失败、阻塞消息及确认结果分开统计。每 10 秒经统一缓冲采样到 DuckDB，历史不用于恢复在线状态；配置重建开启新会话。配置查询掩码隐藏凭据，原掩码保存保留密码。消费页分区编辑与队列配置摘要见 [settings 设计](../settings/sdd.md) 和 [UI 场景](../settings/bdd.md)，不改变消费语义。

- 阶段诊断分别保留连接、解析、持久化、确认的最近失败及恢复时间；解析原因使用安全分类和 JSON 行列位置，不返回载荷。阻塞状态按原消息关联，记录阻塞数量与最早阻塞时间；持久化等待记录最早在途时间。

- 未恢复失败优先于消费状态；已恢复诊断只留历史。`MqRetry` 初始/封顶间隔可配置，稳定正常进展一分钟后重置；管理员立即重试由管理器串行重建整个实例，旧任务未退出不启动新任务。坏格式会持续阻塞重试，需修复解析规则或生产端数据；不提供自动忽略或推进。
- 消费通知默认关闭，复用 Settings.Shared Sender；按实例＋实际主题＋渠道预留 SQLite 冷却窗口（默认十分钟），窗口内失败合并计数，下一次允许通知带汇总。发送失败也占预算，不立即重发；通知事件最多2048个、冷却记录最多4096条，达到上限抑制，不阻塞消费。恢复通知本期不发送。

## 已知问题

- OTel Collector / Filebeat / MinIO / Grafana / MQTTX e2e 均待集成窗（T900 统一补验）。
- OTLP/HTTP 仅鉴权到接入 Key（无 per-tenant 隔离）；内网单机定位下可接受。
