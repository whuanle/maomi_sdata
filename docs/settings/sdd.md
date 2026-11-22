# settings — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[存储布局](../storage-layout.md)、[CQRS 约定](../cqrs-conventions.md)

## 目标

系统设置页（`/settings`，管理员）：把原本只能改 `configs/system.yaml` + 重启的运行参数（S3 冷存储、三段保留、数据压缩、OTLP 导出）搬到 UI，按功能卡片组织；S3/保留/压缩**立即生效**，OTLP **重启生效**。MQ 日志源最初为设置页一个 tab，2026-11-16 起迁出为独立「日志消费」页（`/log-consumption`）；2026-11-17 起升级为**多实例 + 每主题解析规则**（`system:mq` 键），保存后由消费管理器**自动热更新**（见 ingest 模块），仍归本模块管理配置读写。

## Go 兼容实现边界（2026-11-22）

- `go/internal/settings` 与 `cmd/sdata/settings_handlers.go` 保持现有 Web/C# 不变：白名单合并响应、嵌套 retention/runtime、codec 转 archiveParquetCodec、秘密保留与脱敏，覆盖写入相同 `system:*` 键；不再导出原始 setting 全表。摄取仅展示启动白名单字段。
- 宿主用 `registerSettingsRoutes` 替换旧路由，Store/Auth 就绪后执行 `initializeSettings`；成功后唯一赋值 `App.Settings`，请求复用该实例，不重新读取签名材料或懒初始化。DuckDB 内存绑定参数即时应用、读回、保存失败回滚，重启按覆盖恢复。其它消费者未接入时返回 **202 + X-SData-Settings-Apply: stored-not-applied + Warning**，只承诺配置已保存，不承诺热更新或重启后应用；接入点为 `Service.Apply`。Go 文件配置模型尚不包含全部 C# 设置，默认视图只合并当前 Go 启动字段与内置业务默认值。
- API Key 支持两用途的 create/rotate/enable/delete/reveal，补齐旧 `agent_key.enabled`，认证哈希仍沿用现有格式；通知支持11种协议、版本冲突、显式 keepSecrets、模板、目标限制及真实发送。Go AES-GCM 密文绑定用途/记录编号，密钥由内部 Go 签名材料域分离派生；不是 C# Data Protection 密钥环的兼容实现。旧哈希可继续鉴权，无法解密的历史恢复材料明确409，不伪造 reveal。数据库及签名材料仍须整体保护。
- MQTT 提供哈希持久化和 `MQTTAuthenticate` 新连接检查；shared/prefix模式拒绝空密码或超过1024个UTF-16码元的密码，none模式不检查密码。只有监听器接入并显式设置 `MQTTAuthApplied` 后才允许保存，未接入返回501。MQ 实例使用 `system:mq`，可读取旧 Go `mq_instances`；消费管理器尚需改读主键并实现规则，不能将配置保存视为消费成功。
- MQ 预览支持 raw/json/regex/csv/logfmt/otlp-json，最多50条，映射失败包含错误记录；syslog、otlp-proto、.NET 自定义时间格式明确501，正则采用 Go RE2 子集。S3连接测试只有配置真实 `Service.TestS3` 适配器后才执行，缺适配器明确501。通知自建目标需宿主传入 `AllowedOrigins`；默认仅允许内置公共渠道，不为兼容而关闭目标检查。
- 历史JSON损坏、内存/压缩/运行参数非法时回退并记录固定设置键；认证损坏记录键后拒绝连接。日志不记录设置值或底层错误文本。`RuntimeOptions(ctx)`（保留`GetRuntime`别名）为告警预算独立读取`system:runtime`，不读取其它设置或查询DuckDB，元数据库读取失败显式返回error；`SendNotification(ctx,id,message)` 为投递提供真实结果，`Success=false`须转为投递失败，不能仅检查error。

## DuckDB 内存

- 管理员 `?tab=duckdb`，GET 合并视图含 `duckDb.memoryLimit/effectiveMemoryLimit`；PUT `/api/settings/duckdb` 仅接收 `memoryLimit`，保存 `system:duckdb`，不开放路径和线程。
- 前端兼容旧版后端缺失/null的 `duckDb`：显示更新并重启后端及重试提示，不初始化表单、不伪造默认容量，其他页签仍可使用；重试获取新契约后恢复表单。
- 容量支持正数 MB/GB/MiB/GiB（最多六位小数，1字节至安全整数上限）；MB/GB 十进制、MiB/GiB 二进制，拒绝无限制和非法历史覆盖，后者回退文件值。
- `DuckDbSettingsService` 单例串行协调：运行应用并读回 → SQLite 保存；失败恢复旧运行参数，恢复失败返回明确503并记录严重日志。Shared 的 `IDuckDbRuntimeSettings` 由连接管理器实现，写门内参数绑定 SET，不重建实例。启动在 Schema 引导前应用合并值；保留文件默认不变。
- 状态及总览读取实际运行快照，不增加轮询；资源面板沿用原15秒采样。限制不是整个进程的硬内存上限，调大需预留宿主/容器和其它组件资源，调小不保证RSS立即下降。

## API & Agents

- 管理员独立页签 `?tab=agents`；GET/POST `/api/settings/api-keys` 管理具名接入与 Agent 凭据。创建/重置使用256位随机秘密，同时保存哈希、识别前缀及 Data Protection 密文。列表仅返回 canReveal 和审计；管理员显式 reveal 查看，沿用无缓存响应；查看不修改状态或更新时间。旧哈希记录须显式重置才可查看。
- 两类凭据隔离；停用/轮换/删除对后续请求立即生效。MCP能力和安全预算见 [Agents](../agents/sdd.md)。成员页权限保持不变。

## 组件

- **运行时设置通道** `SystemSettingsProvider`（Settings.Core，单例）：SQLite `setting` 表键值覆盖 + 文件 `SystemOptions` 回退（懒加载全表缓存，保存即刷新；脏 JSON/库不可用回退文件默认，不阻塞后台 tick）。键：`system:duckdb/s3/retention/compression/mq/otlp`（值 = 选项类型 JSON；`system:kafka/rabbitmq/pulsar` 为旧单实例键，仅作合成回退）。契约 `ISystemSettingsProvider` 在 Settings.Shared，供 Store/Ingest 跨域引用（跨域仅引 Shared，符合分层）。
- **MQ 实例配置** `GetMqInstances()`（读取顺序）：设置键 `system:mq`（`List<MqInstanceOptions>`，多实例主存储）> 文件 `SData:Mq:Instances` 节 > 旧 `system:kafka/rabbitmq/pulsar` 三键**内存合成**（`kafka-default` 等固定 id 1/2/3，仅 Enabled 且连接+topic 齐备才合成）；首次保存 `system:mq` 即完成迁移，旧键此后不再读取。选项类型 `MqInstanceOptions/MqTopicOptions/MqParserOptions` 在 Infra.Configuration.Shared（避免 Infra→Settings 循环引用）。
- **设置 API** `SettingsController`（`/api/settings`，类级 `[Authorize(Roles = SDataRoles.Admin)]`）：GET 合并视图；PUT `s3/retention/compression/otlp`；POST `s3/test`；GET/PUT `mq-instances`（实例列表，全量替换，保存时新实例补雪花 id）；POST `mq-instances/parse-preview`（样例试解析，不落库）。CQRS 命令 + `IModelValidator`（天数 0–36500、codec 白名单、MQ 类型/格式白名单、Name 唯一、启用即连接必填、regex 可编译、csv 分隔符单字符）。旧 `PUT /settings/mq`（三键逐键保存）已随多实例改造移除。
- **消费配置展示**：编辑侧栏按基本信息、连接、认证/发现、队列分区，长字段整行、窄屏单列；解析参数与样例测试可展开。列表分离运行态、连接摘要和队列行，名称后展示有效配置及默认值，沿用语义主题 token；只改变展示，不修改消费或保存契约。
- **消费重试与通知**：实例包含初始/封顶重试秒数（默认5/60）、告警开关（默认关闭）、渠道ID列表和冷却分钟数（默认10）。保存时验证渠道存在且启用，发送前再次核对；通知冷却元数据独立存SQLite，不属于配置JSON。运行及投递语义见 [Ingest设计](../ingest/sdd.md)。
- **消费类型页签**：Status/Kafka/RabbitMQ/Pulsar 使用设计系统 Tabs；Status 为实例/配置主题消费状态和速度总览（详见 Monitor），其余三个页签保留连接与队列配置详情。数量含停用实例，新建默认当前协议，Status 默认 Kafka。
- **解析预览跨域**：接口 `IMqLogPreviewer` 在 Settings.Shared，实现 `MqLogPreviewer` 在 Ingest.Core（包装 `IMqLogDecoder`），DI 跨域经接口（符合分层）。
- **S3 SECRET 即时生效** `S3SecretManager`（Store.Core，实现 Settings.Shared 契约 `IS3SecretManager`）：保存 S3 后重建 DuckDB SECRET；连接测试 = 临时 SECRET + `COPY (SELECT 1) TO 's3://bucket/[prefix/].sdata-conn-test.parquet'`（数百字节对象保留在桶内）。
- **读侧接入**：`ArchiveService`（保留/S3 对象键/归档压缩编码每轮 tick 重读）、`RetentionService`（每轮重读）、`MqConsumerManagerService`（每 10s 重读实例列表，指纹 diff 热更新）、`ConfigureOpenTelemetryModule`（宿主构建期尽力读 `setting` 表的 `system:otlp`，失败回退文件）。
- **密钥掩码**：S3 GET 返回 `******`，空/掩码保留原 SecretKey；MQ GET 克隆配置后将 SASL、管理密码和 AMQP 密码置为 `********`，保存同一实例掩码时恢复已存值（空值表示清除），不修改运行中配置快照。

## 决策

- **外部 OTLP Header**：TraceHeaders/MetricsHeaders 独立保存为 SDK 逗号分隔 key=value；GET/PUT 返回独立掩码快照，缺省/空/掩码保留，ClearTraceHeaders/ClearMetricsHeaders 显式清空，清空地址不删凭据。校验解码后的 ASCII 文本、重复及保留 metadata，非法历史配置只跳过对应外部出口。沿用 SQLite JSON 明文存储，掩码不等于静态加密，需保护数据库与配置文件权限。

- **自身遥测固定本地出口**：宿主始终采集 Trace/Metrics，经 `LocalTraceExporter/LocalMetricExporter` → `IIngestBuffer` 批量落库，不提供本地开关或地址，页面不展示本机遥测提示块。`system:otlp` 和 `SData:OTLP` 仅控制额外外部出口，空值仅关闭外部导出；非法历史地址跳过，不影响本地。指标按现有模型保存，直方图保留 count/sum，不承诺零丢失。

- S3 页与数据保留页共用 `RetentionPlanOptions` 和已保存状态：已保存 Endpoint/Bucket 均非空时，S3 页仅编辑转冷前本地天数，数据保留页仅编辑冷存天数并提供跳转；否则数据保留页完整编辑本地/冷存天数，S3 页不重复展示策略。保存时以已保存策略为底，仅覆盖可见字段，仍提交完整 `{retention: ...}`；不改变后台过期规则。S3 保存成功回写共享状态，失败重新加载确认持久化结果，草稿及连接测试不切换展示。归档天数直接输入，不提供固定一天快捷按钮；保存连接信息保留原分片粒度。

- **SQLite 键值覆盖而非写回 yaml**：不动配置文件，避免文件写回与格式漂移；文件配置仍是回退默认（合并语义：DB 覆盖 > 文件）。
- **复用 SystemOptions 选项类型**（`S3Options`/`RetentionPlanOptions`/`MqInstanceOptions` 等）作为合并返回值，不造平行 DTO；设置页 UI 类型独立在 `ui/src/api/types.ts`。
- **生效分级**：S3（重建 SECRET）/保留/压缩立即生效（后台服务每轮 tick 重读，最长 10 分钟/1 小时）；**MQ 实例保存后热更新**（管理器周期检测，见 ingest）；OTLP exporter 为启动期构建，保存后重启生效。
- **MQ 全量替换保存**（PUT 整个实例列表）而非逐实例 CRUD：与页面一个保存动作一致，天然支持删除（列表中移除即删）；部分生效由管理器指纹 diff 保证只重启受影响实例。
- **页签权限与默认入口**：成员页签全员可见且默认激活；非法、旧 `?tab=status` / `?tab=ingest` 或当前角色不可见的页签回落到成员。摄取缓冲只读页签已移除，概览中的监控展示和后端摄取行为保持不变。管理员页签仅按 `isAdminRole` 显隐，不依赖设置数据加载成功；加载中保留深链选中态，失败显示重试提示，通知渠道独立加载。设置后端读写全部 Admin 授权为权威。服务状态统一在 Overview 展示，设置页不再请求或轮询监控状态。
- **压缩编码**：`CompressionOptions.ArchiveParquetCodec`（snappy 默认），白名单 uncompressed/snappy/gzip/zstd；仅控制 S3 或本地 `data/cold/` 的冷归档 Parquet 文件，不压缩 DuckDB 数据库文件、不影响 SQLite 元数据或未启用归档的数据。中英文页面采用短导语、独立选择/保存区与分隔线下的说明区；说明在桌面双列、窄屏单列展示适用范围/生效时间和逐行编码参考，保留历史文件不重压缩等提示。
- **设置表零迁移**：`SettingEntity` 早已注册（DbSet + 映射），EnsureCreated 建表，无 SchemaReconciler 变更；旧三键无需清理，惰性迁移后自然失效。
- **顺带修复（2026-11-16）**：验证中发现环境库缺 `ColdArchives`/`rum_project` 表（EnsureCreated 对已存在库不补新表 + Reconciler 旧版跳过缺表），归档/保留每轮报 `no such table`——属既有缺口。已扩展 `SchemaReconciler` 整表缺失自愈（`storage-layout.md`），启动即补齐。

## 通知渠道

- 每种类型都可反复「新建实例」，名称在同类型内去除首尾空格后不能重复；实例配置和启停状态独立，编辑时排除自身进行重名检查。已保存实例在对应类型卡片内以紧凑列表逐条展示名称、状态及编辑/启停/删除操作，审计信息移至编辑抽屉，不渲染独立渠道表格或实例大卡片。系统设置移除顶部刷新工具栏及重复的服务状态页签。

- 管理员 Tab `?tab=notifications` 提供 11 类、多实例渠道的新增/编辑/启停/软删除与确认后测试；SQLite `notification_channel` 独立表保存，框架填充审计字段，版本列防止并发覆盖，雪花 id 对外传字符串。
- 契约 `INotificationSender` 在 Settings.Shared，[Alert](../alerts/sdd.md) 按渠道 id 调用；Settings.Core 负责凭据、签名及业务码判断。评估历史、持久队列和有限重试由 Alert 模块承载。
- 全配置使用 Data Protection 保护；响应只返回非秘密字段与已配置秘密的字段名。编辑显式保留/替换/清除，渠道类型固定。密钥环位于 SQLite 同目录 `notification-keys`。
- 公共渠道限制官方 HTTPS origin，自建/自定义目标需部署侧 `NotificationAllowedOrigins` 精确 origin 白名单；连接时检查地址并直连，拒绝本机、链路本地及元数据地址，关闭代理/重定向，10 秒超时、8 并发、64 KiB 响应上限。
- 自定义 JSON 模板只替换字符串值，Header 限 Authorization/X-*；传输直接调用处理器避免 HttpClient 诊断 URI 泄密，不记录远端响应及异常原文。Matrix 首期仅支持未加密房间。

## 风险

- MQ 实例列表整包保存（单键 upsert），无逐键部分生效问题；管理器 10s 检测间隔内连续保存以最后一次为准。
- OTLP 覆盖在宿主构建期读取（模块 ConfigureServices），首次启动无库时回退文件（`TryLoadOtlpOverride` 全 try/catch）。
- Sessions 归档天数在 S3 页直接输入，配置键仍为 Retention.Rum，包含三个 RUM 表族；不增加固定一天快捷按钮。

- MQTT接入认证由设备页管理员入口管理，GET/POST /api/settings/mqtt-auth，复用system:mqtt-auth与缓存刷新。只存哈希与长度，响应不含材料。新连接生效，旧连接需重连；与MQ日志消费、HTTP摄取key无关。损坏或无法加载配置拒绝连接；默认无覆盖时免key。
