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

## Release 直接运行

- 同一 tag 还会生成 `sdata-<版本>-<rid>.zip`（Windows）或 `.tar.gz`（Linux/macOS）和 `SHA256SUMS.txt`；在 GitHub Releases 下载对应架构并核对 SHA-256。产物为 self-contained **单文件**程序（约 72MB，DuckDB 等原生库随包内嵌解压），无需安装 .NET/Node。无需配置额外发布密钥，使用内置 GITHUB_TOKEN；仓库策略需允许 Release 写入。
- RID：win-x64、linux-x64、linux-arm64、osx-x64、osx-arm64。Linux x64 在 Ubuntu 22.04 构建，ARM64 在 Ubuntu 24.04 构建；只支持 glibc，安装目标 .NET 10 支持发行版所需 ICU/OpenSSL/zlib/libstdc++ 等系统依赖。
- GC 默认吞吐优先：runtimeconfig 内置 Server/Concurrent/RetainVM/CpuGroup GC 与 TieredPGO。多环境可在启动前用 `DOTNET_*` 环境变量覆盖（优先于 runtimeconfig）：容器内存受限时设 `DOTNET_GCHeapHardLimitPercent=75` 防 OOM Kill；低并发/小内存环境设 `DOTNET_gcServer=0` 退回 Workstation GC、`DOTNET_gcConcurrent=0` 降低 CPU 占用。环境变量直接透传给单文件进程，无需改包；健康/状态页与 `/metrics` 的 `sdata_gc_collection_count_total{generation}` 可核对回收次数。
- Windows：解压后 `powershell -File .\start.ps1`；Linux/macOS：`./start.sh`。首次隐藏输入管理员密码（至少 12 字符），无人值守时通过密钥注入环境变量 `SDATA_INITIAL_ADMIN_PASSWORD`，不要写入 shell 历史。
- 默认打开 http://localhost:5300，账号 admin。端口 HTTP 5300、MQTT 1883；冲突时先复制模板、设置独立密码及端口，再用绝对 `SDATA_FILE` 指向现有配置。
- 升级：先备份并停止旧进程，将新包解压到新目录，再复制旧 `configs` 和 `data`，不要覆盖运行中的目录。保留 JWT 私钥；脚本不会重写已有配置。两目录必须可写。
- macOS 未 Developer ID 签名/公证，核验来源后按系统安全设置批准运行，不全局关闭 Gatekeeper。Windows ARM64、Alpine/musl 不在支持范围。
- Docker 登录失败不阻断 Release；某个平台失败则不公开新的 Release。工作流重跑可覆盖同名附件，建议用户使用下载文件的校验清单确认完整性。

## 故障与验收

- 缺密钥：核对 SData 仓库、`docker` 环境名称及环境允许的 tag；不将凭据写进 YAML 或构建参数。
- 登录成功但推送失败：核对仓库名、组织权限和 Access Token 写权限。
- 安装依赖失败：本地用 pnpm 10 更新并提交 lockfile，不移除 `--frozen-lockfile`。
- 构建失败：检查类型检查、Node 与 .NET 构建日志；本地 Docker 验证用 `docker build -t sdata:verify .`。
- 发布后使用隔离数据/配置目录验收 `/health`、主界面与 `/rum/probe.js`，确认私钥在首次启动时生成；不挂载生产数据试跑。
- 同时发布多个 tag 不保证执行顺序；如 latest 指向旧版，在对应稳定 tag 的工作流页重新运行，不强推版本 tag。
