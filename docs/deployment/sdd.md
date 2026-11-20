# deployment — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md)
## 发布流水线

- `.github/workflows/docker-publish.yml` 参考 moai，只有版本 tag push 触发；分支 push 不发布。
- GitHub 托管 Ubuntu runner 使用 Buildx 构建根目录 Dockerfile，推送 Docker Hub；仅需 `contents: read` 权限。
- 凭据从 `docker` Environment secrets 或 Repository secrets 读取：`DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`。仓库间不会自动共享 secrets，需在 SData 仓库配置。
- 镜像默认 `<DOCKERHUB_USERNAME>/sdata`，可用 Variables `DOCKERHUB_NAMESPACE` / `DOCKERHUB_REPO` 覆盖；凭据不传入镜像构建参数。
- 接受严格 SemVer：`X.Y.Z` / `vX.Y.Z`，可带预发布与构建元数据；构建元数据不进入镜像标签。生成版本、`sha-<7位>` 标签，仅稳定版更新 `latest`。
- 并发组不主动取消正在发布的构建；GitHub 并发队列不保证版本排序，回发旧稳定 tag 也会更新 latest，回滚或补发时应知悉。
- 固定 `linux/amd64`：Dockerfile 使用 `linux-x64` DuckDB 原生绑定，未声称支持 ARM64。
- GHA 层缓存作用域 `sdata`；关闭 provenance/SBOM 附加 manifest，与 moai 发布方式一致。

## 镜像边界

- Node 22 / pnpm 10 构建时先类型检查，生成主应用和 `wwwroot/rum/probe.js`；.NET 10 分阶段发布宿主。
- `.dockerignore` 排除运行时 RSA 私钥、私有配置、本地前端构建及测试产物。镜像内只带配置模板，运行时私钥首次启动生成。
- 不修改或删除本机私钥；公开仓库使用独立初始提交，不导入旧历史。完整旧历史保留于 Gitea 备份分支，历史密钥仍应按已暴露处理并轮换。

## 独立程序 Release

- `version → build-ui → binaries → publish-release` 与 Docker job 分离；缺 Docker 密钥不阻断二进制发布。默认权限只读，仅汇总 job 可写 Release。
- 五个原生 runner：Windows x64、Linux x64/ARM64、macOS Intel/Apple Silicon。self-contained .NET 10 **单文件**（`PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true`），不裁剪，无需安装 .NET/Node；仍需操作系统原生依赖。实测单文件包约 72MB（旧多文件包约 76MB）；R2R 与单文件压缩经实测反而把体积推到 96MB 且冷启动变慢（解压开销），故显式关闭 `PublishReadyToRun` / `EnableCompressionInSingleFile`。
- 因单文件下 Serilog 无法靠扫描磁盘目录发现 sink 程序集，`MainExtensions.UseSData` 向 `ReadFrom.Configuration` 显式传入 Console/File sink 的 `ConfigurationReaderOptions`，否则启动即抛 "published as single-file"。`runtimeconfig.template.json` 在构建时并入 runtimeconfig（Server/Concurrent/RetainVM/CpuGroup GC + TieredPGO），并通过 `SData.csproj` 排除出发布包。
- `tool/Release/package.py` 仅打包独立 publish 输出、前端 artifact 和脱敏模板；校验入口兼容单文件 apphost（SData/SData.exe）或完整托管三件套（dll/deps/runtimeconfig），二者缺一或半发布态均拒绝。`SData.csproj` 显式禁止发布运行时配置/数据/开发设置及 `runtimeconfig.template.json`。包内不含初始密码、接入 Key、JWT 私钥或数据库。
- Windows zip，Unix tar.gz 保留执行位。最终归档由 `smoke.py` 解压到临时目录，原生启动并验证登录、SQLite/DuckDB、首页和探针；运行副本不重新打包。
- 启动脚本固定 CWD，首次从环境/隐藏输入读取管理员密码并初始化配置；已有配置不覆盖。包内模板无可用凭据，不直接启动裸 apphost。
- 五个目标全部通过才创建 draft、上传附件及 SHA256SUMS.txt，再公开对应 tag Release；同 tag 重跑覆盖同名资产，已有公开 Release 重跑期间资产更新不保证原子性。
- Release 保留原 tag，归档版本去掉前缀 v 但保留构建元数据；预发布标记 prerelease 且不更新 latest。稳定 tag 补发会更新 latest，顺序约束同镜像发布。
- 不支持 Windows ARM64（Kafka 原生依赖不足）和 Linux musl；macOS 包未签名/公证。DuckDB macOS 库含 x64/ARM64，但运行验收仍由对应 runner 完成。
