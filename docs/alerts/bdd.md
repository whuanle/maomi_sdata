# 告警业务场景
关联：[设计](sdd.md) · [验证](tdd.md) · [运维](sop.md) · [搜索](../analytics/sdd.md)

Feature: 搜索次数告警
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
