# agents — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md) · [设置](../settings/sdd.md)

Feature: Agent 只读访问

  @AGENT-S4 @auto:xunit
  Scenario: 管理员查看和重置 Agent 凭据不扩大读取权限
    Given 管理员已创建 Agent 凭据
    When 再次查看或确认重置凭据
    Then 查看保持凭据不变，重置撤销旧值且仍只允许只读 Agent 访问

  @AGENT-S1 @auto:xunit
  Scenario: Agent 凭据与写入凭据隔离
    Given 管理员创建了只读 Agent 凭据
    When Agent 连接并执行日志查询
    Then 可完成握手和查询，匿名或写入凭据被拒绝

  @AGENT-S2 @auto:xunit
  Scenario: 撤销立即影响后续协议请求
    Given Agent 已成功连接
    When 管理员停用凭据
    Then 后续请求被拒绝

  @AGENT-S3 @auto:xunit
  Scenario: 固定工具受参数与执行预算约束
    Given 已存在日志、链路和指标
    When 执行受限查询或取消长查询
    Then 返回有界结果且敏感样例被掩码，取消终止原生工作

  @AGENT-S5 @auto:xunit
  Scenario: AI 发现数据组织与实际热表结构
    Given 观测数据按日期存储且存在内部管理表
    When AI 查看数据源、数据组织、表列表和表结构
    Then 返回真实类型、可空性、业务语义与允许字段，隐藏管理表和内部位置

  @AGENT-S6 @auto:xunit
  Scenario: AI 组合查询聚合与时间对比
    Given 多类观测数据包含重复链路记录、多标签指标和不同项目的浏览器事件
    When AI 过滤明细并执行聚合、依赖统计和等长时间对比
    Then 统计口径明确、项目隔离且重复依赖不被重复计数，零基线变化率为空

  @AGENT-S7 @auto:xunit
  Scenario: 扩展工具遵守读取和输出边界
    Given AI 已获只读访问权限
    When 提交越界字段、非法结构或大结果查询
    Then 拒绝非法请求并对超预算结果标记截断，不输出秘密或原始敏感属性

  @AGENT-S8 @auto:xunit
  Scenario: AI 跨午夜查询冷热数据
    Given 六类数据的前一日分区已归档而当日数据仍在热层
    When AI 按逻辑数据源查询跨午夜的时间范围
    Then 合并冷热结果且热表目录不冒充完整历史目录

  @AGENT-S9 @auto:xunit
  Scenario: AI 发现完整工具和分析入口
    Given AI 已连接且管理员查看连接说明
    When 查看工具能力
    Then 协议公开26个只读工具及结构化参数，页面按用途分组并说明查询顺序
