# deployment — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · [安装部署](../manual/01-getting-started/02-installation.md)

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
