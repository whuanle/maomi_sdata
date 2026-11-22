# ingest — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @INGEST-S8 Go 值级映射 | `go/internal/ingest/mapper_test.go`：三信号 JSON/protobuf/gRPC 值比较、64 位整数、微秒边界、倒序时间、资源/属性/事件、全部 gauge/sum 点及 histogram | 2026-11-22：`go test -count=1 ./internal/ingest` 通过；保留 C# JSON/proto 格式差异，非整站迁移验收 |
| @INGEST-S9 Go 传输边界 | `go/internal/ingest/transport_test.go`：HTTP gzip/Content-Type/响应、坏报文、字节预算、体积上限、缺失缓冲；bufconn 三信号 gzip 与 4 MiB 拒绝 | 2026-11-22：上述 Go ingest 专项通过；HTTP 首次拒绝停止，gRPC 按信号返回拒绝数，超限不入队 |
| @INGEST-S10 Go 认证 | `go/cmd/sdata/ingest_auth_test.go`：`TestOTLPGRPCRequiresIngestCredentials` | 2026-11-22：由宿主集成任务独立执行与回填；本切片未修改认证/路由 |
| @INGEST-PF16 自身遥测降本 | `SelfTelemetryTests`、`LocalMetricExporter` 标签缓存 | 2026-11-21：SelfTelemetryTests 39/39；链路 resource JSON 静态复用，指标标签 JSON 缓存，采集策略与外部出口不变 |
| @INGEST-PF11 接入背压与字节预算 | `IngestBatchTests.SimpleJsonPreflightReturns429WithRetryAfter`、`MqPersistenceTests.ByteBudgetRejectsOversizedBatchAndTracksBufferedBytes`、OTLP/ES 背压回归 | 2026-11-21：简单 JSON 内容超预算返回 429+Retry-After；缓冲估算字节与 256 MiB 默认预算可统计；MQ持久化定向 14/14、Ingest全量 140/140。真实 1 KB/4 KB 长稳与外部压测待 PF02 |
| @INGEST-PF09 MQ 批量入缓冲回压 | `MqLogDecoderTests.EnqueueAllAsync_BufferFull_RetriesUntilAccepted`、`MqPersistenceTests.BatchEnqueuePreservesAcceptedAndDroppedCounts`、MQ 阻塞/持久化回执集 | 2026-11-21：批量拒绝记录可准确回填重试，已接受记录不重复；MQ 解码与阻塞 37/37、Store MQ 持久化 13/13。Kafka offset/RabbitMQ/Pulsar 消费者端批量确认仍待集成验收 |
| @INGEST-PF08 简单 JSON 批量入口降本 | `IngestBatchTests.SimpleJsonAllTypesReturnBatchAcceptedCounts`、`MqPersistenceTests.BatchEnqueuePreservesAcceptedAndDroppedCounts` | 2026-11-21：日志、链路、指标、遥测批量提交用例通过；批量满载计数保持逐条语义。原始 JSON 片段直传与字节预算仍待 PF11，CPU 压测待 PF02 |
| @INGEST-PF12 OTLP HTTP 请求体与事件属性降本 | `OtlpHttpBodyTests`（JSON/protobuf 普通与 gzip 请求体）、`OtlpProtoParserTests.ParseTraces_PreservesMicrosecondsAndEventAttributes` | 2026-11-21：OTLP 相关定向测试 30/30 通过；未做 OTLP CPU 压测对照 |
| @INGEST-S7 外部出口授权 | `SelfTelemetryTests`（HTTP/gRPC 独立 Header、Bearer/Basic/编码、清空覆盖环境配置、401/403及本地独立导出） | 2026-11-20：遥测39/39、此前Ingest全量116/116通过；仅本机测试接收器，无真实外发或重启部署 |
| @INGEST-AUTH1 强制鉴权与协议路由 | IngestAuthenticationIntegrationTests、IngestApiKeyFilterTests | 2026-11-19：HTTP路径与gRPC三信号鉴权11/11，Ingest全量99/99；真实外部Collector未验收 |
| @INGEST-S6 | `SelfTelemetryTests`（空/非法/不可达外部出口、本地映射/背压、真实缓冲到 DuckDB 查询） | 2026-11-19：遥测专项 23/23（含真实 DuckDB、本机 OTLP 双出口及无递归验证）；末轮宿主编译通过、Ingest 全量 96/97，鉴权用例预期放行实际 401。曾受同期 Agents 编译错误阻塞，末轮已消失；未重启部署或真实外发 |
| @INGEST-S1 | `tests/SData.Ingest.Tests/OtlpParserTests.ParseLogs_ExtractsServiceAndSeverity` | 通过（2026-11-15） |
| @INGEST-S2 | `OtlpParserTests.ParseLogs_MapsSeverityNumber` | 通过（2026-11-15） |
| @INGEST-S3 | `OtlpParserTests.ParseTraces_ExtractsSpans` + `ParseMetrics_ExtractsPoints` | 通过（2026-11-15） |
| @INGEST-S4 | `IngestBufferTests.Buffer_EnqueueDrain_CountsDropped` | 通过（2026-11-15） |
| @INGEST-S5 | 手工 curl（401 验证） | 通过（2026-11-15） |
| @INGEST-ES1 | `EsBulkParserTests.Parse_IndexAndCreate_MapsLogRecords` 等 6 例（解析/映射/坏行/配对） | 通过（2026-11-15，Ingest.Tests 17/17） |
| @INGEST-ES4 | `EsBulkParserTests.EnqueueAll_BufferFull_ReturnsRejectSignal`（429 背压语义 + 响应形状） | 通过（2026-11-15） |
| @INGEST-ES6 | Filebeat 直推 e2e | e2e 待集成窗（需外部 Filebeat，见 configs/filebeat-example.yml） |
| @INGEST-G1 | `OtlpGrpcIntegrationTests.Export_ThreeServices_EnqueueRecords`（进程内 Kestrel h2c + GrpcChannel 三通道） | 通过（2026-11-15，Ingest.Tests 25/25） |
| @INGEST-G2 | `OtlpGrpcIntegrationTests.Export_BufferFull_ReportsPartialSuccess` | 通过（2026-11-15） |
| @INGEST-G3 | `OtlpGrpcIntegrationTests.BuiltinGrpcPort_ServesGrpc`（ApplyListeners 内置 4317 端口 h2c） | 通过（2026-11-15） |
| @INGEST-G4 | OTel Collector gRPC 直连 e2e | e2e 待集成窗（见 configs/otel-collector-example.yaml） |
| @INGEST-AUTH1 | `IngestApiKeyFilterTests`（三态提取 6 例 + 401/放行语义） | 通过（2026-11-15） |
| @INGEST-MQ1 | `MqLogDecoderTests`（otlp-proto/otlp-json/json/raw + json 字段映射、来源打标、RFC3339 嗅探） | 通过（2026-11-17，Ingest.Tests 64/64） |
| @INGEST-MQ2 / @INGEST-MQ11 / @INGEST-MQ13 / @INGEST-MQ15 | `MqMessageBlockingTests`（坏格式/空载荷/空结果/部分失败阻塞、原载荷重试、取消及提交前不确认） | 2026-11-20：专项17/17、Ingest全量129/129通过；旧版原文保底确认语义已废止，真实broker故障注入未执行；其余结果见 [Monitor验证](../monitor/tdd.md) |
| @INGEST-MQ3 | `MqLogDecoderTests.EnqueueAllAsync_BufferFull_RetriesUntilAccepted`（假缓冲前 N 次拒绝后退避重试） | 通过（2026-11-17） |
| @INGEST-MQ4 | 宿主启动日志：管理器「未启用实例不建连接」+ 无 broker 连接（本地烟雾验证） | 通过（2026-11-16，多实例改造后待复验） |
| @INGEST-MQ7 | 8 种解析规则样例：`MqLogDecoderTests`（log4j regex + 堆栈续行、csv 列映射、logfmt、syslog 5424/3164、json 映射、级别别名/时间格式/epoch 单测） | 通过（2026-11-17） |
| @INGEST-MQ8 | 管理器协调：`MqConsumerManagerTests`（可运行过滤/指纹稳定即不重启/白名单外 topic 不可运行） | 通过（2026-11-17） |
| @INGEST-MQ9 | 解析预览器：`MqLogDecoderTests.Previewer_RegexSample_ReturnsRecordsOrError`（命中 Success / 未命中 _parse_error） | 通过（2026-11-17） |
| @INGEST-MQ5 | `tool/MqProducer/verify.py`，真实 Kafka SASL | 精确/前缀消费及重启续传通过；broker 重启故障注入未执行（2026-11-18） |
| @INGEST-MQ6 | `tool/MqProducer/verify.py`，真实 RabbitMQ/Pulsar | 精确/前缀消费及服务重启续传通过；broker 故障注入未执行（2026-11-18） |
| @INGEST-MQ10 | `MqTopicMatcherTests` | 精确优先/最长前缀/正则转义通过（2026-11-18） |
| @INGEST-MQ11 | `tests/SData.Store.Tests/MqPersistenceTests.cs` | 回执、并发满载、取消 3/3；Store 全量 128/128（2026-11-18） |
| @INGEST-MQ12 | `tool/MqProducer/verify.py` | 三类精确来源均增长，新主题/队列前缀各 30/30（2026-11-18） |
| @INGEST-MQ13 / @INGEST-MQ14 | `MqRuntimeTests`（未知 JSON、并发/窗口、回执失败与取消） | 通过（2026-11-19，Ingest 73/73）；真实 broker 故障注入未执行 |
| @INGEST-MQ15 | `MqRuntimeTests.DiagnosticsPreserveFailureUntilParsingRecovers` | 通过（2026-11-19，Ingest 74/74）；真实 broker 故障注入未执行 |
| @INGEST-MQ16 / @INGEST-MQ17 | `MqRetryNotificationTests`、`MqRuntimeTests`：封顶退避、取消/稳定恢复、重复立即重试不并发、SQLite冷却跨工作器重建、发送失败/渠道停用 | 通过（2026-11-20）；本轮最终结果见 [Monitor验证](../monitor/tdd.md)，无真实通知外发或broker故障注入 |
| MQ 回归 | Ingest 69/69；前端全量 189/189、消费页 5/5；tsc + vite + Linux publish | 通过；浏览器已登录并确认三类实例、精确/前缀规则、实时落库计数及凭据脱敏；全角色/双主题交互未验收（2026-11-18） |
