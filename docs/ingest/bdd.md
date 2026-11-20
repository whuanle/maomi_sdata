# ingest — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 数据接入

  @INGEST-MQ16 @auto:xunit
  Scenario: 消费失败自动退避并支持立即重试
    Given 消费实例出现未恢复失败
    When 自动重试或管理员重复请求立即重试
    Then 失败优先显示且重试间隔逐步增加至固定上限，同一实例不会并行运行多个消费者

  @INGEST-MQ17 @auto:xunit
  Scenario: 队列失败通知按窗口抑制
    Given 消费实例启用通知且选择有效渠道
    When 同一队列在窗口内反复出现不同阶段的失败并重建消费者
    Then 同一渠道只投递一次且其余失败计入抑制，窗口过后新的失败携带汇总信息，停用渠道不能发送

  @INGEST-S7 @auto:xunit
  Scenario: 外部遥测携带独立授权且拒绝不影响本机
    Given 链路和指标出口配置了不同授权
    When 外部目标接收遥测或拒绝授权
    Then 各出口携带自己的授权，本机链路和指标仍正常写入且无递归

  @INGEST-S6 @auto:xunit
  Scenario: 自身遥测不依赖外部目标
    Given 未配置外部遥测目标或外部目标不可用
    When 服务产生自身链路和指标
    Then 数据仍进入本机缓冲，满载时按既有策略计数丢弃且不产生递归链路

  @INGEST-S1 @auto:xunit
  Scenario: OTLP 日志解析出服务名与级别
    When 提交包含 resource.service.name=order-service 与 severityText=WARN 的 OTLP logs JSON
    Then 解析结果的服务名为 order-service、级别为 WARN、正文与 traceId 正确

  @INGEST-S2 @auto:xunit
  Scenario: severityNumber 映射级别
    When 提交 severityNumber=17 且无 severityText 的日志
    Then 解析级别为 ERROR

  @INGEST-S3 @auto:xunit
  Scenario: OTLP 链路与指标解析
    When 提交 resourceSpans（kind=2, status.code=2）与 gauge/histogram metrics
    Then span 的 kind=server、status=error、时长 5ms
    Then 指标生成 cpu/mem 及 latency.count/latency.sum 序列

  @INGEST-S4 @auto:xunit
  Scenario: 缓冲满负荷丢弃并计数
    Given 缓冲容量 2
    When 连续写入 3 条日志
    Then 第 3 条被丢弃且统计 dropped=1

  @INGEST-S5 @manual
  Scenario: 错误 key 接入被拒
    When 携带无效 X-Api-Key 调用 /api/ingest/iot
    Then 返回 401

  @INGEST-ES1 @auto:xunit
  Scenario: ES Bulk index/create 批量写入日志
    When 提交 NDJSON（index 与 create 动作混排）到 /_bulk
    Then 每条文档解析为日志记录：_index 映射服务名与来源属性、@timestamp/log.level/trace.id 正确
    Then 响应为 ES 形状 {took, errors:false, items:[{index:{status:201}}]}

  @INGEST-ES2 @auto:xunit
  Scenario: update/delete 返回 ES 形状错误项
    When NDJSON 含 update（带 doc 行）与 delete（无文档行）动作
    Then 对应项返回 {status:400, error:{type:"illegal_argument_exception"}} 且后续行配对不错位

  @INGEST-ES3 @auto:xunit
  Scenario: 坏行单项失败不影响其余项
    Given NDJSON 中一条文档行为非法 JSON
    Then 该项失败 parse_exception、errors:true，其余项正常入库

  @INGEST-ES4 @auto:xunit
  Scenario: 缓冲满整批 429 背压
    Given 接入缓冲已满
    When 提交 ES Bulk 批次
    Then 返回 HTTP 429 且错误类型 es_rejected_execution_exception（Filebeat 自动重试）

  @INGEST-ES5 @manual
  Scenario: Filebeat 直推鉴权
    When Filebeat 以 Basic（password=接入 Key）或 X-Api-Key 推送 /_bulk
    Then 鉴权通过并入库（9200 别名端口经 SData:Listeners 配置实现，见 T112）

  @INGEST-ES6 @manual @e2e-待集成窗
  Scenario: Filebeat 直推端到端
    Given SData 开启 9200 别名端口与接入 Key
    When Filebeat 按 configs/filebeat-example.yml 直推 /_bulk
    Then 日志按 _index 落 logs.source 并可查询；缓冲满 429 时 Filebeat 自动重试不丢事件（集成窗补验）

  @INGEST-OT1 @auto:xunit
  Scenario: OTLP/HTTP(proto) logs 报文解析入库
    When 提交 application/x-protobuf 的 ExportLogsServiceRequest（含 gzip 请求体）
    Then 解析出服务名 order-service、severity=WARN、正文与 trace/span 关联（hex 形态与 JSON 路径一致）且属性/资源 JSON 保真

  @INGEST-OT2 @auto:xunit
  Scenario: OTLP proto traces 与 metrics 解析语义对齐 JSON 路径
    When 提交 proto 报文（span kind=Server/status=Error/时长 5ms；gauge 与 histogram metrics）
    Then span 映射 server/error/5000us；指标生成 cpu.usage(gauge) 与 latency.count/latency.sum(histogram) 序列且标签含 service

  @INGEST-OT3 @auto:xunit
  Scenario: 非法 proto 报文拒绝
    When 提交非 proto 字节到 /v1/logs
    Then 返回 400 且错误消息中文（InvalidProtocolBufferException/JSON 异常/解压异常统一捕获）

  @INGEST-OT4 @manual
  Scenario: 标准路径与旧路径并存
    When OTel Collector 推送 /v1/logs|traces|metrics 或旧 /ingest/otlp/v1/*（转发同一处理链）
    Then 双格式（x-protobuf/JSON）与 gzip 均可入库，鉴权沿用 X-Api-Key/Bearer（旧路径保留一个版本周期后废弃，见 T113 收口）

  @INGEST-G1 @auto:xunit
  Scenario: OTLP gRPC 三通道推送入库（T112）
    When OTel Collector 经 gRPC（LogsService/TraceService/MetricsService）推送 proto 报文
    Then 解析与 OTLP/HTTP 同链路（服务名/级别/关联字段一致）逐条入缓冲，partialSuccess 恒为 0

  @INGEST-G2 @auto:xunit
  Scenario: gRPC 缓冲满背压
    Given 接入缓冲已满
    When gRPC Export 推送日志
    Then 响应 partialSuccess.rejected_log_records 计数丢弃条数（Collector 可识别降级）

  @INGEST-G3 @auto:xunit
  Scenario: Listeners 附加端口 gRPC 生效
    Given SData:Listeners 配置 {Port:4317, Protocol:grpc}
    When 服务启动后 gRPC 客户端经 4317 端口 Export
    Then 附加端口提供 gRPC 服务（纯 HTTP/2）且入库正常

  @INGEST-G4 @manual
  Scenario: OTel Collector gRPC 直连验收（e2e 待集成窗）
    When Collector 配置 exporters.otlp endpoint 指向 SData:4317
    Then traces/logs/metrics 三信号经 4317 入库（配置清单见 T113 的 otel-collector-example.yaml）

  @INGEST-AUTH1 @auto:xunit
  Scenario: 接入鉴权三态（T113 收口）
    When 请求分别携带 X-Api-Key、Authorization: Bearer <key>、Basic（username 任意，password=<key>）
    Then 有效接入凭据允许写入，缺失或错误凭据始终被拒绝，旧免鉴权设置不再开放写入

  @INGEST-MQ1 @auto:xunit
  Scenario: MQ 消息多种格式解码入库
    When 消费到载荷分别为 otlp-proto 字节、otlp-json、简单 JSON（对象/数组，或按字段映射提取）与纯文本行
    Then 解析出的日志记录字段正确（服务名/级别/正文/时间戳）且带来源标记（如 kafka:topic）
    Then json 格式行为与 POST /api/ingest/logs 一致（camelCase、缺省值填充）

  @INGEST-MQ2 @auto:xunit
  Scenario: 失败消息保持阻塞且不确认
    Given 消息源配置了解析规则
    When 收到损坏载荷、未知格式、空载荷或部分记录解析失败的消息
    Then 整条原消息保持未确认并退避重试，不入库保底记录也不推进后续消费

  @INGEST-MQ3 @auto:xunit
  Scenario: 缓冲满退避重试不丢数据
    Given 接入缓冲已满
    When 消费到日志消息
    Then 消费暂停退避重试、消息不确认（Kafka 不提交 offset / RabbitMQ 不 Ack / Pulsar 不 Ack）
    Then 缓冲恢复后消息全部入队，语义为 at-least-once 不丢数据

  @INGEST-MQ4 @auto:xunit
  Scenario: 未启用实例不建连接
    Given 无实例配置或实例均 Enabled=false / 缺连接串（默认）
    When 服务启动
    Then 消费管理器空转且不产生任何 broker 连接，宿主正常服务

  @INGEST-MQ7 @auto:xunit
  Scenario: 文本日志解析规则（regex/csv/logfmt/syslog）
    Given topic 分别配置 regex（log4j 形态命名捕获组）、csv（列索引映射）、logfmt（键值映射）、syslog（RFC5424/RFC3164）
    When 消费到对应形态的文本行（含多行消息与异常堆栈续行）
    Then 时间/级别（别名归一 warning→WARN、PRI→级别）/服务/正文按映射提取，未映射字段进 attributes
    Then regex 未匹配的续行并入上一条正文；首行不匹配则标记解析失败并阻塞原消息

  @INGEST-MQ8 @auto:xunit
  Scenario: 多实例配置热更新（管理器协调）
    Given 实例列表含可运行与不可运行（禁用/缺连接/白名单外规则）实例
    When 管理器读取实例列表计算期望集（指纹 = 配置 JSON）
    Then 仅可运行实例进入期望集且指纹稳定；配置变化产生新指纹触发对应实例消费者重启，其余实例不受影响

  @INGEST-MQ9 @auto:xunit
  Scenario: 解析规则测试预览
    When 设置页提交样例文本与解析规则（POST /api/settings/mq-instances/parse-preview）
    Then 返回逐行映射结果（时间/级别/服务/正文/attributes）；规则未命中返回失败原因且行带 _parse_error 标记，不落库不进缓冲

  @INGEST-MQ5 @manual
  Scenario: Kafka 端到端消费
    Given 日志消费页新建 Kafka 实例（broker 地址、topic 与解析规则）并启用
    When otel-collector Kafka exporter 或 kcat 向 topic 生产日志
    Then 10s 内消费者自动启动，日志入库 logs 表并可查询；Kafka 宕机重启后服务存活并自动续消费不丢

  @INGEST-MQ6 @manual
  Scenario: RabbitMQ/Pulsar 端到端消费
    Given 日志消费页分别新建 RabbitMQ/Pulsar 实例（队列/topic 与解析规则）并启用
    When 向队列/topic 生产文本或结构化日志
    Then 手动确认逐条入库可查询；broker 断连恢复后自动续消费

  @INGEST-MQ10 @auto:xunit
  Scenario: 精确规则优先于最长主题前缀
    Given 同一消息源配置精确规则与多个重叠前缀
    When 收到匹配多个规则的日志
    Then 优先使用精确规则，否则使用最长前缀，且只解析一次

  @INGEST-MQ11 @auto:xunit
  Scenario: 日志持久化后才允许确认消息
    Given 日志正在等待批量写入
    When 存储尚未提交或等待被取消
    Then 消息保持未确认，只有成功落库后才完成确认等待

  @INGEST-MQ12 @manual
  Scenario: 三类消息源持续消费与动态发现
    Given 三类消息源均配置主题精确规则和前缀规则
    When 持续发送日志并在运行中新建匹配主题或队列
    Then 三类来源日志均可查询，新增主题或队列按对应前缀规则消费

  @INGEST-MQ13 @auto:xunit
  Scenario: 消费统计区分解析质量与持久化确认
    Given 一个消息源配置了主题规则
    When 收到正常消息和未知形状的结构化消息
    Then 未知形状计解析失败并保持未确认，只有整条消息完整解析并提交后才增加落库计数

  @INGEST-MQ15 @auto:xunit
  Scenario: 失败诊断独立于后续消息保留
    Given 消费消息解析失败并保持阻塞
    When 原消息重试后完整解析成功
    Then 最近失败的原因和时间仍可查询，恢复时间单独记录且失败期间没有确认

  @INGEST-MQ14 @auto:xunit
  Scenario: 主题速率空闲归零且会话隔离
    Given 多个实际主题按同一前缀规则消费
    When 持续空闲超过一分钟或重新启动消费者
    Then 空闲速率归零而会话累计保留，重新启动使用独立会话且不复活旧连接状态
