# deployment — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md)
## GitHub 配置

1. 在 SData 仓库 Settings → Environments 创建 `docker`，按需配置审批者及允许的发布 tag。
2. 在该环境添加 `DOCKERHUB_USERNAME` 和 `DOCKERHUB_TOKEN`（Docker Hub Access Token，Read & Write）；也可放 Repository secrets。同名时环境密钥优先。
3. 可选：Settings → Secrets and variables → Actions → Variables 配置 `DOCKERHUB_NAMESPACE`（组织命名空间）和 `DOCKERHUB_REPO`（默认 `sdata`）。名称使用小写，不填 registry 域名。
4. 确保 Docker Hub 目标仓库存在且该账号有推送权限。moai 的 secrets 不会自动共享到此仓库。

## 发布

- 将工作流和待发布代码提交后，创建并推送版本 tag，例如 `v1.2.3` 或 `v1.2.3-rc.1`。
- `git tag v1.2.3` → `git push origin v1.2.3`；只推指定 tag，避免批量推送旧 tag 误改 latest。
- 工作流只接受严格 SemVer；`v1.2.3+build.7` 的镜像版本为 `1.2.3`，构建元数据不会进入 Docker tag。
- Actions 的 `release` 中 Docker job 成功后，在 Summary 查看版本、SHA 标签和 digest。只有稳定版更新 latest。
- 拉取 `<namespace>/sdata:<version>`；覆盖仓库变量时同步替换镜像名。生产建议固定版本或 digest。
- 目前只支持 `linux/amd64`，不要在 ARM64 机器上当作原生镜像使用。

## Docker 运行

- 推荐在仓库根目录执行 `docker compose up -d`。也可以直接运行：

  ```bash
  docker run -d --name sdata -p 5300:5300 \
    -v "$PWD/data:/app/data" \
    -v "$PWD/configs:/app/configs" \
    <namespace>/sdata:<version>
  ```

- 必须映射两个目录，且都要持久化、容器进程可写：
  - `data/` → `/app/data`：SQLite（`sdata.db`）与 DuckDB（`sdata.duckdb`）。删除即丢失全部日志、链路、指标和账号。
  - `configs/` → `/app/configs`：`appsettings.json`（覆盖配置）与 `rsa_private.key`（JWT 私钥）。删除私钥后，已登录的 token 全部失效。
- `configs/` 可以是空目录。缺少 `configs/appsettings.json` 时，启动会把镜像内的 `appsettings.json` 复制过去；之后直接编辑该文件覆盖任意参数，优先级仍低于环境变量 `SData__*`。可调启动参数（端口、DuckDB 资源、接入缓冲等）推荐放 `configs/system.json`（键形如 `{"SData": {…}}`），设置页「启动配置」页签提供模板与当前生效值，重启生效；优先级 `appsettings.json < configs/appsettings.json < configs/system.json < 环境变量`。
- 容器端口须与 `SData:Port` 一致；修改端口时同时改 `ports` 映射与 `healthcheck` 中的地址。4317/4318/9200 标准采集端口内置固化，需要对外暴露采集时同样在 `ports` 中映射。
- 管理员初始密码 `SData:Auth:InitialAdminPassword` 默认为 `sdata@123`，首次启动前在 `configs/appsettings.json` 或环境变量 `SData__Auth__InitialAdminPassword` 中修改。它只在数据库没有任何用户时生效，已有数据库需在页面修改。
- 升级镜像：保留 `data/` 和 `configs/`，不要删除 `configs/appsettings.json`。它是首次启动时的完整快照，新版本调整的默认值不会覆盖已复制的同名键，新增键仍回落到新镜像。升级后对照新版镜像内的 `appsettings.json` 合并差异；也可以备份后删除该文件，让它按新默认值重新生成，再把自定义项填回。
- 启动失败且日志出现 `UnauthorizedAccessException`（或 `Permission denied`）时，通常是宿主机挂载目录对容器用户不可写，Docker 自动创建的挂载目录属于 root。先执行 `docker compose run --rm --entrypoint id sdata` 查看容器用户的 UID/GID，再执行 `sudo chown -R <uid>:<gid> data configs`。

## Release 直接运行

- 同一 tag 还会生成 `sdata-<版本>-<rid>.zip`（Windows）或 `.tar.gz`（Linux/macOS）和 `SHA256SUMS.txt`；在 GitHub Releases 下载对应架构并核对 SHA-256。产物为 self-contained **单文件**程序（exe 约 72MB，解压后目录约 80MB，压缩包约 69MB；DuckDB 等原生库随包压缩内嵌，首次启动时解压，比之后启动约慢 0.5 秒），无需安装 .NET/Node。无需配置额外发布密钥，使用内置 GITHUB_TOKEN；仓库策略需允许 Release 写入。
- RID：win-x64、linux-x64、linux-arm64、osx-x64、osx-arm64。Linux x64 在 Ubuntu 22.04 构建，ARM64 在 Ubuntu 24.04 构建；只支持 glibc，安装目标 .NET 10 支持发行版所需 ICU/OpenSSL/zlib/libstdc++ 等系统依赖。
- GC 默认吞吐优先：runtimeconfig 内置 Server/Concurrent/RetainVM/CpuGroup GC 与 TieredPGO。多环境可在启动前用 `DOTNET_*` 环境变量覆盖（优先于 runtimeconfig）：容器内存受限时设 `DOTNET_GCHeapHardLimitPercent=75` 防 OOM Kill；低并发/小内存环境设 `DOTNET_gcServer=0` 退回 Workstation GC、`DOTNET_gcConcurrent=0` 降低 CPU 占用。环境变量直接透传给单文件进程，无需改包；健康/状态页与 `/metrics` 的 `sdata_gc_collection_count_total{generation}` 可核对回收次数。
- Windows：解压后双击 `SData.exe`；Linux/macOS：在解压目录执行 `./SData`。无需启动脚本。技术参数全部代码内置默认：端口、存储路径、日志级别（Debug 构建 Information / Release 构建 Warning）、DuckDB 资源、缓冲与预算等不再进 `appsettings.json`；文件里只保留初始管理员密码这一个引导参数，需要覆盖任意默认值时在 `configs` 目录新建 `system.json`（设置页「启动配置」页签有模板与当前生效值，重启生效）或用环境变量（优先）。有页面入口的设置（S3 冷存储、保留、压缩、OTLP、日志消费、DuckDB 内存上限、通知、RUM 站点、接入 Key）在页面修改并保存到 SQLite，已保存的值优先。
- 默认打开 http://localhost:5300，账号 admin，初始密码为 `appsettings.json` 中的 `SData:Auth:InitialAdminPassword`（发布包内的值 `sdata@123`），首次登录后在页面修改。HTTP 端口为 `SData:Port`（默认 5300，可用环境变量 `SData__Port` 覆盖）；标准采集端口 4317（OTLP gRPC）/4318（OTLP HTTP）/9200（ES `_bulk`）内置固化、始终开启、不可配置；MQTT 默认 1883，用环境变量 `SData__Mqtt__Port` 覆盖（层级以 `__` 连接）。
- 升级：先备份并停止旧进程，将新包解压到新目录，再复制旧 `data`、`configs/rsa_private.key`（JWT 私钥）和旧的 `configs/appsettings.json`（如有），不要覆盖运行中的目录。若旧部署用环境变量覆盖过参数，新的启动环境需保留同样的变量；改过旧包 `appsettings.json` 中的 `SData:Port` 的，新包里也要改成同样的值。`data/` 与 `configs/` 必须可写。
- macOS 未 Developer ID 签名/公证，核验来源后按系统安全设置批准运行，不全局关闭 Gatekeeper。Windows ARM64、Alpine/musl 不在支持范围。
- Docker 登录失败不阻断 Release；某个平台失败则不公开新的 Release。工作流重跑可覆盖同名附件，建议用户使用下载文件的校验清单确认完整性。

## 故障与验收

- 缺密钥：核对 SData 仓库、`docker` 环境名称及环境允许的 tag；不将凭据写进 YAML 或构建参数。
- 登录成功但推送失败：核对仓库名、组织权限和 Access Token 写权限。
- 安装依赖失败：本地用 pnpm 10 更新并提交 lockfile，不移除 `--frozen-lockfile`。
- 构建失败：检查类型检查、Node 与 .NET 构建日志；本地 Docker 验证用 `docker build -t sdata:verify .`。
- 发布后使用隔离数据/配置目录验收 `/health`、主界面与 `/rum/probe.js`，确认私钥在首次启动时生成；不挂载生产数据试跑。
- 同时发布多个 tag 不保证执行顺序；如 latest 指向旧版，在对应稳定 tag 的工作流页重新运行，不强推版本 tag。
