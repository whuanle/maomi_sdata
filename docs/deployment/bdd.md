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

  @DEP-S6 @manual
  Scenario: 五个平台的独立程序发布
    Given 推送了合法版本标记
    When 各平台构建且验证最终压缩包成功
    Then 对应版本提供五个独立运行包与校验清单且预发布不覆盖稳定版

  @DEP-S6 @auto:python
  Scenario: 单文件独立包体积与启动验证
    Given 使用单文件+原生库内嵌（不裁剪/R2R/单文件压缩）发布
    When 打包后由 smoke 验证原生启动、DuckDB 查询与登录
    Then 包体积约 72MB 且冷启动约 2 秒，GC 配置已并入 runtimeconfig 且模板不进包

  @DEP-S7 @manual
  Scenario: 程序包首次启动及升级
    Given 用户已提供独立管理员密码
    When 首次运行启动入口并在升级后再次启动
    Then 首次生成配置和本机密钥且再次启动不覆盖原配置和数据

  @DEP-S8 @manual
  Scenario: 独立程序不依赖镜像发布凭据
    Given Docker Hub 凭据缺失或登录失败
    When 各平台程序包构建与验证成功
    Then 程序包仍可发布到对应版本的下载附件

  @DEP-S9 @manual
  Scenario: 不发布未验证的程序包
    Given 某一目标平台构建或启动验证失败
    When 发布流水线汇总结果
    Then 不公开新的完整下载版本
