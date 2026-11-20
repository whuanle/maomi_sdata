# deployment — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 版本镜像发布

  @DEP-S1 @manual
  Scenario: 稳定版本发布
    Given 发布者已配置镜像仓库凭据
    When 推送合法稳定版本标记
    Then 发布版本和提交标识镜像并更新最新稳定版

  @DEP-S2 @manual
  Scenario: 预发布版本不覆盖稳定版
    Given 镜像仓库已有最新稳定版
    When 推送合法预发布版本标记
    Then 仅发布该版本和提交标识镜像且稳定版不变

  @DEP-S3 @manual
  Scenario: 非发布变更不推送镜像
    Given 发布流水线已经启用
    When 仅推送分支或提交非法版本标记
    Then 分支不触发发布且非法版本在推送镜像前被拒绝

  @DEP-S4 @manual
  Scenario: 缺失凭据时停止发布
    Given 发布凭据不完整
    When 尝试发布版本镜像
    Then 发布明确失败且日志不输出凭据内容

  @DEP-S5 @manual
  Scenario: 镜像包含探针且隔离运行时秘密
    Given 构建机器存在本地配置和运行时私钥
    When 从源码构建发布镜像
    Then 镜像包含前端和浏览器探针且不包含本地私钥或私有配置
