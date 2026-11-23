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

  @DEP-S16 @manual
  Scenario: 镜像推送前冒烟门禁与体积可见
    Given 发布流水线构建的镜像已载入构建机本地（暂不推送）
    When 容器启动后 60 秒内 /health 就绪
    Then 冒烟通过才逐标签推送 Docker Hub；镜像体积与 manifest digest 记录到发布 Summary，冒烟失败禁止推送

  @DEP-S17 @manual
  Scenario: 非发布变更持续验证镜像
    Given 推送 main 分支或提交 PR（纯文档变更跳过）
    When docker-verify 工作流构建 Dockerfile（不推送）并运行容器
    Then 冒烟 /health 通过且镜像体积写入构建 Summary；GHA 缓存 scope 与发布流水线共用，发布 tag 构建直接命中热缓存

  @DEP-S6 @manual
  Scenario: 五个平台的独立程序发布
    Given 推送了合法版本标记
    When 各平台构建且验证最终压缩包成功
    Then 对应版本提供五个独立运行包与校验清单且预发布不覆盖稳定版

  @DEP-S15 @auto:python
  Scenario: 单文件独立包体积与启动验证
    Given 使用单文件、原生库内嵌与单文件压缩发布（不裁剪、不做 R2R）
    When 打包后由 smoke 验证原生启动、DuckDB 查询与登录
    Then 压缩包约 69MB、解压后约 80MB，热启动约 2 秒，GC 配置已并入 runtimeconfig 且模板不进包

  @DEP-S10 @auto:python
  Scenario: 本地残留的前端产物不进入程序包
    Given 发布目录中残留旧版本的前端静态文件
    When 使用本次前端产物打包
    Then 程序包的网页根目录只包含本次前端产物

  @DEP-S7 @manual
  Scenario: 程序包首次启动及升级
    Given 用户解压程序包到独立目录，参数全部使用内置默认值
    When 直接运行 SData 可执行文件，并在升级后再次启动
    Then 首次启动生成本机 JWT 私钥，两次启动均可用且不覆盖 data 与 configs/rsa_private.key

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

  @DEP-S11 @auto:xunit
  Scenario: 空配置目录首次启动补齐默认配置
    Given 配置目录为空且程序目录存在默认配置
    When 服务首次启动
    Then 配置目录生成与默认配置内容一致的覆盖配置文件

  @DEP-S12 @auto:xunit
  Scenario: 用户修改的覆盖配置重启后保持不变
    Given 配置目录中的覆盖配置已被用户修改
    When 服务再次启动
    Then 用户修改的配置内容保持不变

  @DEP-S13 @manual
  Scenario: 容器映射数据与配置目录后重建仍可用
    Given 用户将数据目录与配置目录映射到宿主机且两个目录均为空
    When 容器首次启动并完成管理员登录，随后删除并重建容器
    Then 数据库、覆盖配置与私钥保留在宿主机两个目录中，重建后原有数据仍可查询

  @DEP-S14 @auto:xunit
  Scenario: 工作目录缺少默认配置时不生成配置目录
    Given 服务从不含默认配置文件的目录启动
    When 服务加载配置
    Then 该目录下不生成配置目录
