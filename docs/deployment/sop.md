# deployment — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md) · [安装部署](../manual/01-getting-started/02-installation.md)

## GitHub 配置

1. 在 SData 仓库 Settings → Environments 创建 `docker`，按需配置审批者及允许的发布 tag。
2. 在该环境添加 `DOCKERHUB_USERNAME` 和 `DOCKERHUB_TOKEN`（Docker Hub Access Token，Read & Write）；也可放 Repository secrets。同名时环境密钥优先。
3. 可选：Settings → Secrets and variables → Actions → Variables 配置 `DOCKERHUB_NAMESPACE`（组织命名空间）和 `DOCKERHUB_REPO`（默认 `sdata`）。名称使用小写，不填 registry 域名。
4. 确保 Docker Hub 目标仓库存在且该账号有推送权限。moai 的 secrets 不会自动共享到此仓库。

## 发布

- 将工作流和待发布代码提交后，创建并推送版本 tag，例如 `v1.2.3` 或 `v1.2.3-rc.1`。
- `git tag v1.2.3` → `git push github v1.2.3`；只推指定 tag，避免批量推送旧 tag 误改 latest。
- 工作流只接受严格 SemVer；`v1.2.3+build.7` 的镜像版本为 `1.2.3`，构建元数据不会进入 Docker tag。
- Actions 的 `docker-publish` 成功后，在 Summary 查看版本、SHA 标签和 digest。只有稳定版更新 latest。
- 拉取 `<namespace>/sdata:<version>`；覆盖仓库变量时同步替换镜像名。生产建议固定版本或 digest。
- 目前只支持 `linux/amd64`，不要在 ARM64 机器上当作原生镜像使用。

## 故障与验收

- 缺密钥：核对 SData 仓库、`docker` 环境名称及环境允许的 tag；不将凭据写进 YAML 或构建参数。
- 登录成功但推送失败：核对仓库名、组织权限和 Access Token 写权限。
- 安装依赖失败：本地用 pnpm 10 更新并提交 lockfile，不移除 `--frozen-lockfile`。
- 构建失败：检查类型检查、Node 与 .NET 构建日志；本地 Docker 验证用 `docker build -t sdata:verify .`。
- 发布后使用隔离数据/配置目录验收 `/health`、主界面与 `/rum/probe.js`，确认私钥在首次启动时生成；不挂载生产数据试跑。
- 同时发布多个 tag 不保证执行顺序；如 latest 指向旧版，在对应稳定 tag 的工作流页重新运行，不强推版本 tag。
