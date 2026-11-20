# iot — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)（四件套随 T142 收尾补齐）

Feature: 物联网设备接入

  @IOT-CRED1 @auto:xunit
  Scenario: 已注册启用设备免密码接入
    Given 设备已经注册且启用
    When 设备使用自身标识连接而不提供密码
    Then 连接通过且无需创建或读取密钥

  @IOT-CRED2 @auto:xunit
  Scenario: 旧哈希工具保持兼容
    Given 历史设备密钥哈希工具仍可使用
    When 生成随机密钥并计算哈希
    Then 哈希可正确校验且不影响免密码设备接入

  @IOT-SEEN1 @auto:xunit
  Scenario: LastSeen 内存累积批量回写
    Given 设备 30 秒内多次上报（同设备取最新）
    When DeviceLastSeenFlusher 周期 Drain 批量 UPDATE
    Then 单事务批量回写成功，逐消息零 SQLite 写入；不存在的设备不计入

  @IOT-SEEN2 @auto:xunit
  Scenario: 二次回写覆盖旧值
    When 同设备两批先后回写
    Then LastSeenTime 为第二批时间

Feature: 内嵌 MQTT broker（T140）

  @IOT-B1 @auto:xunit
  Scenario: 设备按主题约定推送单值遥测入库
    Given 已注册启用设备使用自身标识免密码连接内嵌服务
    When 发布 iot/dev1/temperature 主题，消息体 {"v": 21.5, "q": 0}（QoS1）
    Then 遥测进入接入缓冲：设备 dev1、信号 temperature、数值 21.5
    Then 发布收到成功 PUBACK

  @IOT-B2 @auto:xunit
  Scenario: 批量 JSON 数组消息体逐条入库
    When 发布 iot/dev1/batch 主题，消息体为 JSON 数组（元素自带 signal）
    Then 数组每项生成一条遥测记录进入缓冲

  @IOT-B11 @auto:xunit
  Scenario: 同一设备多主题多形态混推
    Given 设备 dev1 已连接
    When 依次发布 iot/dev1/temperature（单值对象）、iot/dev1/line1/pressure（裸数字多级信号）、iot/dev1/batch（批量数组）
    Then 四条遥测全部进入缓冲，多级信号名原样保留为 line1/pressure

  @IOT-B12 @auto:xunit
  Scenario: 两设备同名信号按 device_key 隔离
    Given 设备 dev1 与 dev2 均已连接
    When dev1 与 dev2 分别发布各自主题下的同名信号 temperature
    Then 两条遥测 signal 均为 temperature 且 device_key 各自正确、互不覆盖

  @IOT-B3 @auto:xunit
  Scenario: retain 支持
    When 发布消息带 retain 标记
    Then 后续设备订阅该主题即收到保留消息

  @IOT-B4 @auto:xunit
  Scenario: 禁用设备不能通过上报恢复
    Given 设备已被禁用
    When 设备尝试重新连接或继续发布
    Then 接入被拒且设备保持禁用

  @IOT-B5 @auto:xunit
  Scenario: 越权主题发布被拒
    Given 设备 dev1 已连接
    When 发布 iot/dev2/... 主题（非自身 deviceKey）
    Then PUBACK 返回 NotAuthorized 且不入缓冲

  @IOT-B6 @auto:xunit
  Scenario: 非法消息体被拒
    When 发布消息体不是协议约定 JSON（如 not-json）
    Then PUBACK 返回 PayloadFormatInvalid 且不入缓冲

  @IOT-B7 @auto:xunit
  Scenario: 缓冲满背压
    Given 接入缓冲已满
    When 设备发布遥测
    Then PUBACK 返回 QuotaExceeded（设备端可重试）且不入缓冲

  @IOT-B8 @auto:xunit
  Scenario: 凭证校验器未就绪时 fail-closed
    Given MQTT broker 已随服务启动但设备凭证服务未注册
    When 设备连接
    Then 连接被拒（NotAuthorized），不发生任何数据放行

  @IOT-B10 @auto:xunit
  Scenario: 真实设备台账免密码联通
    Given 服务已注册设备台账校验器
    When 已启用设备免密码连接并发布自身信号
    Then 遥测进入缓冲且禁用设备无法连接

  @IOT-B9 @auto:xunit
  Scenario: 内嵌服务始终自动开启
    Given 未设置启停配置或保留任意旧启停值
    When SData 启动
    Then 内嵌服务监听设备接入端口并接受符合认证策略的连接

  @IOT-M1 @manual
  Scenario: MQTTX 直连验证（e2e 待集成窗）
    Given 客户端以合法的新设备标识连接并符合接入认证策略
    When 连接并发布 iot/{deviceKey}/{signal} 单值与批量消息
    Then 遥测出现在数据查询页面（连接清单见 configs/mqtt-example.md，T142 提供）

  @IOT-S5 @auto:xunit
  Scenario: 首次有效上报自动注册新设备
    Given 新设备标识合法并通过接入认证
    When 首次发布自身信号的有效遥测
    Then 设备自动注册并启用，默认名称为设备标识，遥测进入缓冲且活跃时间批量更新

  @IOT-S6 @auto:xunit
  Scenario: 连接与非法消息不产生设备台账
    Given 设备尚未注册
    When 仅建立连接或使用错误密钥、越权主题、非法消息体
    Then 不创建设备台账且非法消息不进入缓冲

  @IOT-S7 @auto:xunit
  Scenario: 重复及并发首次上报只创建一台设备
    Given 多个连接使用同一合法设备标识
    When 同时或重复上报有效遥测
    Then 仅存在一条设备台账且已有名称分组与标签不被覆盖

  @IOT-S8 @auto:xunit
  Scenario: 注册失败拒绝发布且可重试
    Given 首次注册的元数据存储暂时失败
    When 设备上报后在存储恢复时重试
    Then 首次消息拒绝且不更新活跃时间，重试成功后遥测进入缓冲

  @IOT-S9 @auto:xunit
  Scenario: 删除不保留台账且重新上报重新注册
    Given 设备已完成有效上报
    When 成员删除设备后设备再次通过认证发布有效遥测
    Then 旧台账彻底移除且生成新标识编号的默认设备台账，历史遥测保留

  @IOT-S10 @auto:xunit
  Scenario: 在线连接遵循设备管理状态变化
    Given 同一设备已有多个连接并已上报
    When 成员禁用设备后连接继续上报
    Then 所有连接的新消息被拒且台账不会通过上报重新启用

Feature: 遥测统一查询（T311，契约 C1）

  @IOT-SEARCH1 @auto:xunit
  Scenario: 设备遥测 DSL 查询（POST /api/iot/search）
    When 提交 range/filters（device_key prefix、num_value eq）与 groupBy signal + sum(num_value)
    Then 返回列式结果，信号分组求和正确

  @IOT-SEARCH2 @auto:xunit
  Scenario: 查询跨度约束与空表短路
    When 查询跨度超过 MaxQueryDays 或区间内无天表
    Then 分别返回 400 与空行（TablesScanned=0）

  @IOT-S1 @manual
  Scenario: 查询与新建相邻
    Given 成员打开设备管理页
    When 输入条件并点击查询或按回车
    Then 查询回到第一页且查询与新建按钮在同一工具条相邻显示

  @IOT-S2 @manual
  Scenario: 设备遥测数据源
    Given 已有数值和字符串设备遥测
    When 成员在图表探索选择 MQTT 并保存到仪表盘后重开
    Then 字段、过滤与图表定义保留且设备台账和遥测仍分层存储

  @IOT-S11 @manual
  Scenario: 设备遥测抽屉展示推送主题清单
    Given 设备推送过多个不同主题（含多级信号与字符串状态量）
    When 成员打开该设备的遥测抽屉
    Then 清单展示每个主题的完整主题串、最后值与最后推送时间，点击数值主题行切换上曲线，时间窗可在 1 小时/1 天/3 天/7 天间切换

  @IOT-S3 @auto:xunit
  Scenario: 三种接入认证应用于新连接
    Given 设备标识合法且未被禁用，管理员已配置认证方式
    When 设备使用空密码、统一密钥或匹配前缀连接
    Then 仅符合所选策略的连接被接受且修改策略后新连接遵循新值

  @IOT-S4 @manual
  Scenario: 请求异常直接展示实际错误
    Given 设备页请求当前后端尚未提供的接入认证接口
    When 接口返回网页内容而非有效数据
    Then 页面直接展示实际解析错误，不替换为版本或重启建议
