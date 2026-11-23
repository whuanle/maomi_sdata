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
- `.github/workflows/docker-verify.yml`（push main / PR 触发，纯文档变更跳过）：构建 Dockerfile（不推送）→ 容器 `/health` 冒烟 → 镜像体积写入 Summary；GHA 缓存 scope 与发布共用 `sdata`，发布 tag 构建直接命中热缓存。
- `build-and-push` 推送前门禁：镜像先载入构建机本地冒烟（60s 内 `/health` 200），通过才逐标签 `docker push`——坏镜像不会到达 Docker Hub；Summary 记录镜像体积（MB）与 manifest digest（`docker buildx imagetools inspect`，替代 buildx 原地推送时不可用的 `outputs.digest`）。

## 镜像边界

- Node 22 / pnpm 10 构建时先类型检查，生成主应用和 `wwwroot/rum/probe.js`；.NET 10 分阶段发布宿主。
- 运行层 = `debian:bookworm-slim` + 自包含单文件产物（不再基于 `aspnet:10.0` 共享运行时）：backend-build 阶段以 `--self-contained true -r linux-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugType=None` 发布——托管程序集与运行时压缩进单文件 `SData`，原生库（libduckdb 等）松散随 apphost 放置（与旧框架依赖发布同布局，加载路径已被现网验证），启动零自解压开销、不依赖 /tmp 可执行；镜像内不带 PDB。
  - 与 CI 独立包的差异：独立包用 `IncludeNativeLibrariesForSelfExtract=true` 换取单文件便携性（运行时解压到临时目录）；镜像内不需要该便携性，关闭自解压换启动速度与稳定性（/tmp noexec 环境不受影响）。不裁剪、不 R2R 的原因同独立包（Maomi/MCP 反射；R2R 只增体积）。
- 运行层自带依赖（apt，`--no-install-recommends`）：`ca-certificates`（S3 https 端点）、`libssl3`/`zlib1g`/`libstdc++6`（原生库运行依赖）、`tzdata`、`wget`（docker-compose healthcheck）。musl/Alpine 不支持（DuckDB 等原生库仅出 glibc 绑定），与独立包口径一致。
- 全球化运行于 invariant 模式（`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`，镜像内不装 libicu，省约 40MB）：业务代码全部显式 InvariantCulture、无 TimeZoneInfo 用法，且容器内本就无 locale（CurrentCulture 恒为 invariant，行为与旧镜像一致）。需恢复完整 ICU：apt 追加 `libicu72` 并删除该环境变量。
- `.dockerignore` 排除运行时 RSA 私钥、私有配置（含 `/configs/appsettings.json`）、本地前端构建及测试产物。镜像内 `appsettings.json` 含控制台日志与无页面入口参数（均有默认值）；运行时私钥与 `configs/appsettings.json` 均在首次启动生成。
- `ENTRYPOINT ["./SData"]`（自包含 apphost，不再 `dotnet SData.dll`）；VOLUME/EXPOSE/前端内嵌方式与配置快照逻辑均不变。
- 不修改或删除本机私钥；公开仓库使用独立初始提交，不导入旧历史。完整旧历史保留于 Gitea 备份分支，历史密钥仍应按已暴露处理并轮换。

## 配置与持久化目录

- 容器声明 `VOLUME /app/data` 与 `/app/configs`；`docker-compose.yml` 以 bind mount 映射 `./data`、`./configs`。两者都是运行状态，须持久化且对容器进程可写（操作见 [sop](sop.md)）。
- 配置优先级：程序目录 `appsettings.json`（镜像或包内）< `configs/appsettings.json` < 环境变量 `SData__*` < 命令行。
- 补齐规则（`MainExtensions.EnsureConfigsAppSettings`）：`configs/appsettings.json` 不存在时，复制内容根目录的 `appsettings.json`，配置目录不存在则先创建；已存在则不覆盖；内容根缺少 `appsettings.json` 时不做任何事，也不创建 `configs/`。复制为字节级拷贝，首次启动的生效配置与默认值相同。
- 取舍：选择完整快照，是为了让运维直接在文件中看到并修改全部参数。代价是镜像升级后，`configs/` 中已存在的同名键仍以旧值为准，需人工合并；新增键照常回落到新镜像默认值。
- 复制失败（目录只读或无权限）时启动即失败并指向该文件，不静默降级。便携包同样适用：包内不含 `configs/`，首次运行时生成。

## 独立程序 Release

- `version → build-ui → binaries → publish-release` 与 Docker job 分离；缺 Docker 密钥不阻断二进制发布。默认权限只读，仅汇总 job 可写 Release。
- 五个原生 runner：Windows x64、Linux x64/ARM64、macOS Intel/Apple Silicon。self-contained .NET 10 **单文件**（`PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true` + `EnableCompressionInSingleFile=true`），不裁剪、不做 R2R，无需安装 .NET/Node；仍需操作系统原生依赖。win-x64 实测：压缩后 exe 72MB、解压后目录 80MB、压缩包 69MB；不压缩则 exe 166MB、解压后目录 174MB（压缩包仍约 69MB，下载大小几乎不变）。压缩的代价是运行时私有内存约 +59MB、工作集约 +98MB（实测 173→271MB），首次解压启动多约 0.5 秒，热启动无差异；考虑 [DuckDB 内存复盘](../store/memory-root-cause.md) 后仍选择开启，因为省的是每台部署的常驻磁盘，且内存增量在运行预算内。R2R 只会变大（单独 231MB，与压缩叠加 96MB），显式关闭 `PublishReadyToRun`。`PublishTrimmed` 的 full/partial 模式可把压缩后 exe 降到 32/41MiB，但两者启动即崩溃（Maomi 模块构造函数反射、MCP 的 System.Text.Json 反射），在补齐源生成与根标注前不启用。
- 因单文件下 Serilog 无法靠扫描磁盘目录发现 sink 程序集，`MainExtensions.UseSData` 向 `ReadFrom.Configuration` 显式传入 Console sink 的 `ConfigurationReaderOptions`，否则启动即抛 "published as single-file"。日志只输出到控制台，已移除 `Serilog.Sinks.File`。`runtimeconfig.template.json` 在构建时并入 runtimeconfig（Server/Concurrent/RetainVM/CpuGroup GC + TieredPGO），并通过 `SData.csproj` 排除出发布包。
- `tool/Release/package.py` 仅打包独立 publish 输出、前端 artifact 和包内 README；发布目录中的 `wwwroot` 不带入（本机构建残留的旧哈希资源会与新前端并存），网页根只取 `--ui` 的前端产物。校验入口兼容单文件 apphost（SData/SData.exe）或完整托管三件套（dll/deps/runtimeconfig），二者缺一或半发布态均拒绝。`SData.csproj` 显式禁止发布运行时配置/数据/开发设置及 `runtimeconfig.template.json`。包内只有 `appsettings.json`（无页面入口参数的默认值与控制台日志），`configs/appsettings.json` 不随包发布，不含接入 Key、JWT 私钥或数据库。
- Windows zip，Unix tar.gz 保留执行位。最终归档由 `smoke.py` 解压到临时目录，直接运行 `SData.exe`/`SData`，用内置默认密码登录并验证 SQLite/DuckDB、首页和探针；同时断言包内无 `configs/`、无启动脚本，运行目录无日志文件；首次启动后 `configs/appsettings.json` 与包内 `appsettings.json` 逐字节一致。运行副本不重新打包。
- 无启动脚本：直接运行包内 `SData.exe`/`SData`，双击即可。参数内置默认值；`appsettings.json` 放无页面入口的参数，首次启动复制生成的 `configs/appsettings.json` 覆盖它，环境变量 `SData__*` 优先；宿主不读取 `SDATA_FILE` 与 `configs/system.*`。RSA 私钥仍写入 `configs/rsa_private.key`（运行时生成）。已知限制：内容根取当前工作目录，从其它目录启动时读不到程序目录的 `appsettings.json`（控制台无日志），`data/` 与 `configs/` 会写入该目录；双击或在程序目录内启动不受影响。
- 五个目标全部通过才创建 draft、上传附件及 SHA256SUMS.txt，再公开对应 tag Release；同 tag 重跑覆盖同名资产，已有公开 Release 重跑期间资产更新不保证原子性。
- Release 保留原 tag，归档版本去掉前缀 v 但保留构建元数据；预发布标记 prerelease 且不更新 latest。稳定 tag 补发会更新 latest，顺序约束同镜像发布。
- 不支持 Windows ARM64（Kafka 原生依赖不足）和 Linux musl；macOS 包未签名/公证。DuckDB macOS 库含 x64/ARM64，但运行验收仍由对应 runner 完成。
