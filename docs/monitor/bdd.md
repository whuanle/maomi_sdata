# monitor — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/bdd.md)

Feature: 服务状态与总览聚合

  @MON-S8 @auto:e2e
  Scenario: 共享持久化故障恢复后不再显示提示
    Given 共享写入器曾发生失败且后续批次已提交
    When 管理员查看日志消费状态
    Then 不显示已恢复故障的提示、原因和时间，只有仍未恢复的持久化故障显示告警


  @MON-MQ7 @auto:xunit
  Scenario: 立即重试消费实例需要确认
    Given 消费实例出现失败并等待自动重试
    When 管理员确认立即重试
    Then 请求重建整个实例且旧消费者退出前不会启动另一个，状态刷新重试与消费重试分开

  @MON-MQ8 @auto:xunit
  Scenario: 通知抑制状态可见
    Given 已启用消费失败通知
    When 同一队列持续失败
    Then 可见通知投递结果、冷却截止时间及被抑制次数且不暴露通知凭据

  @MON-MQ5 @auto:xunit
  Scenario: 消费失败可按阶段诊断
    Given 主题解析失败或正在等待持久化与消息确认
    When 查看消费状态并展开最近失败
    Then 可见阶段、等待时长、原因、发生时间和阻塞消息数，成功恢复不删除最近失败

  @MON-MQ6 @auto:xunit
  Scenario: 状态查询失败可定位并重试
    Given 状态请求超时、被拒绝或返回无效响应
    When 页面刷新失败
    Then 展示安全错误类别、可用的状态码和发生时间且允许重试，不暴露原始响应或凭据

  @MON-MQ2 @auto:xunit
  Scenario: 消费状态总览与历史隔离
    Given 存在当前会话与历史消费采样
    When 查看消费状态
    Then 只展示仍配置的实例并区分禁用与空闲，历史采样不恢复当前在线状态

  @MON-MQ3 @auto:xunit
  Scenario: 状态页汇总主题并保留协议详情
    Given 已配置多种消息源
    When 打开日志消费页面
    Then 默认展示 Status 的主题速率和解析质量且不含连接明细，协议详情仍在三个原有页签

  @MON-S5 @auto:e2e
  Scenario: 每个配置队列或主题独立展示消费状态
    Given 同一实例配置了消费中、空闲、异常和尚无快照的队列或主题
    When 查看消费总览或协议详情
    Then 每个名称展示自身状态、速率及错误，前缀规则汇总匹配数量而不展开实际主题

  @MON-S7 @auto:e2e
  Scenario: 消费失败直接显示红色警告
    Given 队列或主题存在解析失败、确认失败或最近失败记录
    When 查看消费总览或协议详情
    Then 未恢复失败显示消费失败而非正在消费，恢复后历史失败保留但不影响当前健康状态

  @MON-S6 @auto:e2e
  Scenario: 消费速率精度适合阅读
    Given 消费速率包含长小数或极小正值
    When 查看消费速率
    Then 消息与记录速率最多两位小数且字节速率最多一位，去除尾零，极小正值不显示为零

  @MON-MQ4 @auto:xunit
  Scenario: 消费状态获取失败不显示健康零速率
    Given 已配置消费实例
    When 状态刷新失败
    Then 保留已有快照并提示过期，未知速率不显示为零

  @MON-S4 @auto:xunit
  Scenario: macOS 系统资源采样
    Given 服务运行在 macOS
    When 后台完成资源采样
    Then 系统 CPU 与物理内存通过系统原生接口获取，可用内存按空闲与非活跃页估计且不重复累计

  @MON-S2 @auto:xunit
  Scenario: 系统资源与应用进程分别统计
    Given 服务完成后台资源采样
    When 查看总览
    Then 分别展示系统与应用的 CPU、内存和线程指标，首次 CPU 差分完成前显示未知

  @MON-S3 @auto:xunit
  Scenario: 存储资源显示实际消耗与采样状态
    Given 存储后台采样已完成
    When 查看总览
    Then 展示磁盘容量和可用空间、DuckDB 追踪内存与文件大小以及 S3 对象总量和检查状态

  @MON-MQ1 @manual
  Scenario: 消息队列消费状态可追踪
    Given 消息队列消费者正在运行
    When 查看服务状态和日志消费页面
    Then 可见实例的落库计数、最后落库时间及最近错误且不含连接凭据

  @MON-S1 @auto:xunit
  Scenario: 服务状态详情
    When 查询 /api/monitor/status
    Then 返回服务名/版本/运行时长/进程内存/GC 堆/线程数与各域今日行数

  @MON-O1 @auto:xunit
  Scenario: 总览聚合通道健康
    Given 接入缓冲中 log 通道已落库 100 条、rumevent 通道丢弃 5 条
    When 查询 /api/overview/status
    Then 通道表含七个数据类型通道，log 通道健康（无丢弃）、rum_event 通道标记不健康
    Then 各通道今日行数与天表数量随当日天表实际状态返回

  @MON-O2 @auto:xunit
  Scenario: 总览聚合存储用量
    When 查询 /api/overview/status
    Then 返回热层 DuckDB 配置路径与内存上限；数据库文件存在时返回文件字节数，不存在时为 0
    Then S3 标志反映有效配置是否完整，不代表已验证连通性

  @MON-O3 @auto:xunit
  Scenario: 总览页前端呈现
    Given 后端返回通道健康表（含丢弃通道）与存储用量
    When 打开总览页
    Then 展示系统信息、七类数据统计、共享缓冲、热层用量和 S3 配置状态，不展示最近日志或样本图

  @MON-O4 @auto:xunit
  Scenario: 总览展示安全的队列实例摘要
    Given 有启用、禁用和尚无运行快照的队列实例
    When 打开总览页
    Then 按配置实例展示名称、类型、最近状态、进程累计落库数及时间，排除已删除实例且不含连接参数和原始异常

  @MON-O5 @auto:xunit
  Scenario: 刷新失败不伪装成最新健康
    Given 已加载成功的总览数据
    When 后续刷新部分或全部失败
    Then 提示失败来源与旧快照，无法获取的指标为未知且存活探针失败不继续标为存活
