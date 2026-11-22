# settings — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 系统设置

  @SET-S55 @manual
  Scenario: Go 后端按接入认证模式检查密码长度
    Given 已启用共享密码或前缀认证
    When 设备提交空密码或超过支持长度的密码
    Then 拒绝连接，免认证模式不受该密码长度检查影响

  @SET-S54 @manual
  Scenario: Go 后端诊断损坏配置时不泄露内容
    Given 已保存的历史设置包含损坏或非法值
    When 读取设置或接入认证配置
    Then 回退默认设置或拒绝认证，并仅记录配置标识而不记录秘密内容

  @SET-S49 @manual
  Scenario: Go 后端设置视图不泄露内部秘密
    Given 管理员已有冷存储和外部遥测凭据
    When 查询设置或用掩码再次保存
    Then 展示结构化设置与已配置标记，不返回内部签名材料，原凭据保持不变

  @SET-S50 @manual
  Scenario: Go 后端明确区分保存配置和实际生效
    Given 部分运行功能尚未接入设置消费者
    When 管理员保存对应设置或发起尚未支持的连接测试
    Then 保存结果明确标记尚未生效，未实现的测试明确拒绝，不宣称连接成功

  @SET-S51 @manual
  Scenario: Go 后端管理凭据与通知渠道
    Given 管理员创建了按用途隔离的凭据和具名通知渠道
    When 轮换凭据或保留通知秘密并使用旧版本重复更新
    Then 旧凭据失效，列表不包含秘密，过期版本不能覆盖新配置

  @SET-S52 @manual
  Scenario: Go 后端消费规则预览如实报告失败
    Given 管理员编辑包含字段映射的消费实例
    When 保存掩码凭据并预览有效、无效或暂未支持的样例
    Then 凭据保留且展示映射记录和失败原因，不把失败样例伪装成解析成功

  @SET-S53 @manual
  Scenario: Go 后端未接入监听器时不声称启用了接入认证
    Given 监听器尚未接入新的认证检查
    When 管理员尝试修改接入认证
    Then 明确拒绝变更且不保存会造成认证已生效误解的配置

  @SET-S48 @manual
  Scenario: 后端尚未提供内存设置时页面仍可使用
    Given 服务端尚未提供数据库内存设置
    When 管理员打开内存页签并在服务更新后重试
    Then 页面提示更新服务且不展示伪造配置，其他设置可用，重试成功后恢复真实配置表单

  @SET-S45 @auto:xunit
  Scenario: 管理员调大数据库内存限制
    Given 数据库使用较小的内存限制
    When 管理员手动调大内存并保存
    Then 新限制立即生效且重新加载后保留，其他启动参数不变

  @SET-S46 @auto:xunit
  Scenario: 非法容量和保存失败不覆盖原设置
    Given 数据库已有生效的内存限制
    When 输入非法容量或运行应用及持久保存失败
    Then 拒绝非法输入且失败时恢复原运行设置，无法恢复时明确报错

  @SET-S47 @manual
  Scenario: 内存配置权限与资源说明
    Given 用户打开系统设置
    When 管理员进入数据库内存页签或非管理员访问该入口
    Then 仅管理员可调整并查看生效值和资源余量提示，非管理员回落到成员页签

  @SET-S43 @auto:xunit
  Scenario: 管理员可查看及重置集成凭据
    Given 管理员已创建可恢复的接入凭据或 Agent 凭据
    When 再次查看并确认重置
    Then 查看返回原秘密且不修改状态，重置返回新秘密并立即撤销旧值

  @SET-S44 @auto:xunit
  Scenario: 旧凭据不能从哈希恢复
    Given 凭据仅有历史哈希或恢复材料损坏
    When 管理员请求查看
    Then 提示重置且原凭据的鉴权和状态保持不变

  @SET-S42 @auto:xunit
  Scenario: 消费告警只允许选择有效通知渠道
    Given 管理员编辑消费实例的重试与通知设置
    When 保存已启用通知及所选渠道
    Then 有效渠道和重试参数持久保存，已停用或不存在的渠道被拒绝且保存本身不发送通知

  @SET-S41 @auto:xunit
  Scenario: 外部遥测授权按出口独立保护
    Given 链路和指标出口分别配置了授权信息
    When 管理员查询、替换或明确清空其中一份授权
    Then 查询只返回掩码，另一份授权保留且本机遥测不受影响

  @SET-S40 @auto:xunit
  Scenario: 本机遥测固定启用且外部出口可选
    Given 自身链路和指标正在写入本机
    When 管理员配置或清空外部遥测出口
    Then 本机遥测始终启用且没有修改入口，页面不展示本机遥测提示块，外部出口独立生效

  @SET-S38 @auto:xunit
  Scenario: 集成凭据按用途隔离并可立即撤销
    Given 管理员创建具名接入凭据和只读 Agent 凭据
    When 再次查询列表或轮换、禁用、删除凭据
    Then 列表不包含秘密且旧凭据立即失效，两类凭据不能交换用途

  @SET-S39 @manual
  Scenario: 独立管理 API 与 Agents
    Given 设置数据加载失败
    When 管理员打开 API 与 Agents 页签
    Then 仍可管理凭据并查看客户端指引，非管理员不能访问

  @SET-S32 @manual
  Scenario: 设置页移除只读摄取缓冲页签
    Given 管理员已登录
    When 打开系统设置或旧摄取缓冲链接
    Then 不展示摄取缓冲页签及只读面板，旧链接回落到成员页签

  @SET-S30 @auto:xunit
  Scenario: 告警复用通知渠道但不暴露凭据
    Given 已存在启用的通知渠道
    When 用户为告警选择渠道
    Then 仅返回渠道名称和类型及状态，不返回凭据

  @SET-S29 @manual
  Scenario: 设置加载失败不隐藏管理员页签
    Given 管理员打开系统设置
    When 设置数据仍在加载或加载失败
    Then 全部八个页签保持可见且保留选中的页签，失败可重试并可独立打开通知渠道

  @SET-S28 @manual
  Scenario: 服务状态仅在概览展示
    Given 用户已登录
    When 打开系统设置或旧服务状态链接
    Then 默认显示成员页签且不再展示或请求服务状态

  @SET-S21 @auto:xunit
  Scenario: 管理独立通知渠道并保护凭据
    Given 管理员已保存带凭据的通知渠道
    When 保留原凭据修改渠道并再次查询
    Then 凭据不以明文返回且版本递增

  @SET-S22 @auto:xunit
  Scenario: 停用或删除的通知渠道不能发送
    Given 已配置可发送的通知渠道
    When 管理员停用或删除渠道
    Then 后续通知不再向该渠道发送

  @SET-S23 @auto:xunit
  Scenario: 测试草稿不改变已保存配置
    Given 管理员正在编辑通知渠道草稿
    When 执行测试发送
    Then 测试结果不泄露凭据且草稿不落库

  @SET-S24 @auto:xunit
  Scenario: 拒绝未授权或敏感的通知目标
    Given 部署管理员设置可信通知目标
    When 配置不可信目标或目标解析到本机及元数据地址
    Then 通知发送被拒绝

  @SET-S27 @manual
  Scenario: 通知实例在对应类型内展示
    Given 管理员已创建多个不同类型的通知实例
    When 打开系统设置的通知渠道页签
    Then 顶部没有刷新工具栏且实例仅在对应类型内逐条紧凑展示名称、状态和操作，审计信息在编辑时查看

  @SET-S26 @auto:xunit
  Scenario: 每种渠道支持多个不同名称的实例
    Given 管理员已创建一个通知渠道实例
    When 为同一渠道类型新建另一个不同名称的实例
    Then 两个实例具有独立配置和启停状态且同类型重名保存被拒绝

  @SET-S25 @manual
  Scenario: 管理员配置十一类通知渠道
    Given 管理员打开通知渠道页签
    When 选择渠道类型并填写必填配置后保存
    Then 渠道出现在列表且保存不会自动发送通知

  @SET-S18 @manual
  Scenario: 在冷存储页配置一天自动归档
    Given 管理员已启用日志冷存储
    When 在天数输入框直接填写一天并保存
    Then S3 页保留该天数，数据保留页仅显示冷存天数且原值保持不变

  @SET-S19 @auto:xunit
  Scenario: 保存连接信息保留已有归档分片粒度
    Given 冷存储已按小时分片
    When 管理员保留密钥掩码并保存连接信息
    Then 原密钥和小时分片方式保持不变

  @SET-S1 @manual
  Scenario: 管理员查看系统设置合并视图
    When 以 admin 打开 /settings 页面
    Then 加载 GET /api/settings（200），页面按卡片展示：冷存储 S3 / 数据保留（热/冷三段）/ 数据压缩 / 自身遥测 OTLP（MQ 日志源已迁出至 /log-consumption「日志消费」页，仍走同一接口）
    And SecretKey 以掩码 ****** 返回且 hasSecret=true

  @SET-S2 @manual
  Scenario: 非管理员只能查看成员
    Given 当前用户不是管理员
    When 打开系统设置
    Then 仅显示成员页签且不展示管理员设置

  @SET-S3 @manual
  Scenario: 保存 S3 冷存储立即生效
    When admin 填写 Endpoint/Bucket/Prefix/AccessKey/SecretKey 并保存（PUT /api/settings/s3）
    Then 返回 200 且立即重建 DuckDB SECRET（无需重启）
    And 超过热存期的天表在下一轮归档归档为 s3://bucket/{prefix}/{domain}/{batchId}/{day}.parquet
    When 开关关闭并保存（endpoint 置空）
    Then 关闭冷层，归档回退本地 data/cold/ 目录

  @SET-S4 @manual
  Scenario: S3 连接测试
    When admin 在 S3 卡片点「测试连接」（POST /api/settings/s3/test）
    Then 以临时 SECRET 写入 .sdata-conn-test.parquet 验证连通性，返回 success/message
    And 失败（端点/凭据错误）返回 success=false 与中文原因，不影响已运行的冷层 SECRET

  @SET-S5 @manual
  Scenario: 密钥掩码语义
    When admin 再次保存 S3 且 SecretKey 传掩码或空
    Then 后端沿用已存 SecretKey（GET 仍返回掩码）

  @SET-S6 @manual
  Scenario: 修改三段保留天数
    When admin 修改某域热/冷天数并保存（PUT /api/settings/retention）
    Then 返回 200，下一轮保留/归档 tick（最长 1 小时/10 分钟）按新天数执行
    And 冷存天数 0 = 热到期直接删除不归档

  @SET-S7 @manual
  Scenario: 修改数据压缩编码
    When admin 选择 zstd/gzip/snappy/uncompressed 并保存（PUT /api/settings/compression）
    Then 下一轮归档 COPY 使用新编码；非法编码（非白名单）被 400 拒绝且运行时回退 snappy
    And 历史归档对象不重写

  @SET-S8 @manual
  Scenario: MQ 实例保存后自动热更新（无需重启）
    When admin 在 /log-consumption「日志消费」页新建/编辑/停用/删除实例并保存（PUT /api/settings/mq-instances）
    Then 返回 200 并持久化到设置键 system:mq；消费管理器 10s 内检测到指纹变化，自动启停对应实例消费者
    And OTLP 导出地址保存后仍为重启生效

  @SET-S9 @manual
  Scenario: 参数校验
    When 天数超界（<0 或 >36500）、MQ 实例类型/解析格式非白名单、OTLP 协议非法
    Then 返回 400 业务错误（中文消息）

  @SET-S10 @auto:xunit
  Scenario: MQ 实例列表保存与校验
    When admin 保存实例列表（Kafka/RabbitMQ/Pulsar 各一，名称唯一、启用实例连接齐备、regex 可编译）
    Then 校验通过；新实例 Id=0 由后端补雪花 id
    When 名称重复、未知类型、已启用缺连接、regex 不可编译、csv 分隔符多字符、格式非白名单
    Then 校验拒绝（400，中文消息）

  @SET-S11 @manual
  Scenario: 旧单实例配置兼容回退
    When 环境存在旧 system:kafka/rabbitmq/pulsar 覆盖且未保存过 system:mq
    Then GetMqInstances 内存合成单实例列表（kafka-default 等），消费行为与升级前一致
    When 首次在页面保存实例列表
    Then system:mq 落库即完成迁移，旧键不再读取

  @SET-S12 @manual
  Scenario: 解析规则测试预览
    When admin 在 topic 编辑区粘贴样例日志点「测试解析」（POST /api/settings/mq-instances/parse-preview）
    Then 返回逐行映射结果（时间/级别/服务/正文/attributes）；规则未命中的行原文入库并标记 _parse_error
    And 预览不写入 DuckDB、不进摄取缓冲

  @SET-S13 @auto:xunit
  Scenario: 消费配置凭据以掩码展示并可原样保存
    Given 消息源已保存连接凭据
    When 管理员查看配置并保留掩码保存
    Then 页面不获得明文密码，已存凭据保持不变

  @SET-S14 @manual
  Scenario: 按消息队列类型切换实例
    Given 已配置多种类型的消息队列实例，部分实例已停用
    When 管理员切换消息队列类型页签
    Then 仅显示该类型实例，各页签数量包含已停用实例且状态页签显示总数

  @SET-S16 @manual
  Scenario: 消费实例与队列配置分层展示
    Given 管理员配置多个消息源及不同解析规则的队列
    When 在亮色或暗色主题查看实例列表
    Then 每个队列名称后展示匹配方式、解析格式、默认服务和级别，以及当前格式生效的解析参数，连接凭据保持脱敏

  @SET-S17 @manual
  Scenario: 分区编辑队列配置并取消草稿
    Given 管理员打开实例编辑侧栏
    When 按基本信息、连接、认证和队列规则分区编辑并取消
    Then 原实例不变，解析参数和样例测试可展开，窄屏字段单列且保存操作保持可见

  @SET-S15 @manual
  Scenario: 在类型页签中新建实例
    Given 管理员正在查看 RabbitMQ 类型页签
    When 新建消息队列实例
    Then 新建表单默认选择 RabbitMQ

  @SET-S31 @manual
  Scenario: 冷归档压缩说明避免数据库压缩歧义
    When 管理员打开数据压缩页签
    And 在独立操作区选择编码，在桌面双列或窄屏单列说明区阅读适用范围、生效时间及逐行编码参考
    Then 页面明确仅压缩 S3 或本地冷归档文件，不压缩 DuckDB 数据库文件且不影响 SQLite 元数据；下一轮归档生效，已有归档不重压缩，未启用归档的数据不受影响

  @SET-S20 @manual
  Scenario: 设置四类数据一天归档
    Given 管理员已配置冷存储
    When 分别将日志、指标、链路、会话本地保留天数填写为一天并保存
    Then S3 页显示已保存的本地保留天数，数据保留页仅显示冷存期限且原值不变，会话规则包含事件与回放

  @SET-S33 @manual
  Scenario: 按已保存的 S3 状态拆分策略入口
    Given 管理员已启用并保存 S3
    When 打开 S3 页和数据保留页
    Then S3 页仅编辑转冷前本地天数，数据保留页仅编辑冷存天数并可跳转管理转冷时间

  @SET-S34 @manual
  Scenario: 未启用 S3 时管理完整本地保留策略
    Given 管理员未启用 S3
    When 打开数据保留页
    Then 完整显示本地和冷存天数并说明本地冷目录，S3 页不重复展示策略

  @SET-S35 @manual
  Scenario: 分页保存不覆盖隐藏的策略字段
    Given 管理员已配置五类数据的保留策略
    When 在 S3 页和数据保留页交替修改并保存各自可见的天数
    Then 隐藏字段及其他类型原值保持不变，包含冷存为零的类型

  @SET-S36 @manual
  Scenario: S3 草稿与连接测试不切换策略展示
    Given 管理员正在修改 S3 开关或连接信息
    When 仅修改草稿或测试连接后再保存
    Then 草稿和测试不改变策略展示，保存成功同步展示，保存失败后重新读取已持久化状态

  @SET-S37 @auto:xunit
  Scenario: 接入认证只存哈希并保留同模式密钥
    Given 管理员在设备页设置统一密钥或前缀
    When 保存后在同模式留空再次保存
    Then 原认证材料保留且返回值不包含密钥，损坏配置不会回退为免密
