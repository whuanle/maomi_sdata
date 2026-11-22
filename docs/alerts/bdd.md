# 告警业务场景
关联：[设计](sdd.md) · [验证](tdd.md) · [运维](sop.md) · [搜索](../analytics/sdd.md)

Feature: 搜索次数告警
  @PF13-ALT @auto:xunit
  Scenario: 告警评估不占普通查询槽
    Given 普通用户查询槽已满且告警专用槽空闲
    When 调度器评估告警计数
    Then 告警可进入专用槽执行且普通查询预算与告警预算互不占用

  @ALT-S1 @auto:xunit
  Scenario: 连续命中后触发且持续异常不重复通知
    Given 已启用连续两个窗口命中的告警
    When 两个完整窗口均达到次数阈值
    Then 告警触发且持续异常不重复通知

  @ALT-S2 @auto:xunit
  Scenario: 恢复与评估失败区分
    Given 告警已经触发
    When 评估失败
    Then 告警不会被误判为恢复

  @ALT-S3 @auto:xunit
  Scenario: 限制执行成本
    Given 告警与预览共用执行预算
    When 已有评估正在执行
    Then 新请求被拒绝且不会并行扫描

  @ALT-S4 @auto:xunit
  Scenario: 旧规则通知失效
    Given 通知尚未发送
    When 对应规则被停用或更新
    Then 旧版本通知不会继续发送

  @ALT-S5 @auto:xunit
  Scenario: 旧数据库升级
    Given 数据库尚无告警元数据
    When 系统重复执行结构修补
    Then 告警表和索引完整且修补幂等

  @ALT-S6 @manual
  Scenario: 搜索条件完整冻结
    Given 搜索包含属性条件和侧栏过滤及降噪
    When 保存搜索与告警
    Then 告警保留全部有效条件且不自动发送测试通知

  @ALT-S7 @auto:xunit
  Scenario: 保存搜索快照与规则版本保持一致
    Given 使用者创建告警且尚未保存搜索
    When 保存后重新打开系统并修改告警
    Then 搜索快照和精确标识被保留且旧版本不能覆盖修改

  @ALT-S8 @auto:xunit
  Scenario: 无效规则不能留下孤立搜索
    Given 告警引用不存在或停用的通知渠道
    When 使用者保存启用告警
    Then 保存被拒绝且不会留下新的搜索快照

  @ALT-S9 @auto:xunit
  Scenario: 计数只覆盖指定时间且标明下界
    Given 日志包含窗口内外的记录及多个绑定条件
    When 预览或评估次数达到计数上限
    Then 只统计窗口内记录且结果明确标为下界

  @ALT-S10 @auto:xunit
  Scenario: 投递失败在重启后仍受重试上限约束
    Given 触发通知失败且恢复通知正在等待
    When 系统重启后继续投递
    Then 触发通知最多尝试三次且恢复通知不越过待重试的触发通知

  @ALT-S11 @auto:xunit
  Scenario: 通知拥塞不能丢失状态转换
    Given 通知队列已经达到上限
    When 告警满足触发条件且后续窗口仍然命中
    Then 队列恢复容量后会重试触发转换而不是丢弃通知

  @ALT-S12 @auto:xunit
  Scenario: 查看历史不暴露通知凭据
    Given 只读使用者查看告警且可写使用者选择通知渠道
    When 系统返回历史与可选渠道
    Then 历史保留最近四十八个评估点和二十次投递且渠道不含凭据
