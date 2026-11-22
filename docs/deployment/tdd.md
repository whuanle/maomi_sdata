# deployment — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| 单文件独立产物 + GC 配置 | `tool/Release/package.py`、`tool/Release/smoke.py`、`tool/Release/test_release.py`；`src/SData/runtimeconfig.template.json` | 2026-11-21：CI 已开启单文件压缩。win-x64 压缩版发布→打包→smoke 通过；GC 配置并入 runtimeconfig 且模板不进包。手工基准（未入库）：压缩 exe 72.2MiB、压缩包约 69MiB、解压目录 79.7MiB（116 文件）；不压缩 exe 166.2MiB、解压目录 173.6MiB（压缩包仍约 69MiB，下载大小几乎不变）；压缩的运行时私有内存 +59MiB、工作集 +98MiB，冷启动 +0.5s；R2R 231.0MiB（与压缩叠加 96.1MiB）；裁剪 full/partial+压缩为 32.3/41.4MiB 但启动即崩溃。热启动 ≈2.1s，首次解压启动 ≈4.4s |
| @DEP-S15 | win-x64 压缩包 smoke：解压后原生启动并验证登录、SQLite/DuckDB、首页、探针；体积与启动时间见上行 | 通过（2026-11-21）：当前源码压缩版连续 5 次全部通过、无进程占用清理失败（此前旧 smoke.py 经 start.ps1 启动时曾出现，重写为直接启动 apphost 后不复现）；内置默认密码登录、仅控制台日志、双击模拟（工作目录为程序目录）通过，从其它目录启动见 sdd 已知限制；Linux/macOS 待 CI |
| @DEP-S10 | `test_release.py::test_stale_publish_web_root_is_replaced_by_release_ui`：发布目录残留旧前端文件时，包内仅保留本次 UI 产物 | 通过（2026-11-20）；回退为旧拷贝逻辑时该用例失败，已验证 |
| @DEP-S1 / @DEP-S2 | 安全解析工作流 YAML，执行版本解析 Bash：稳定、预发布、构建元数据共 5 组 | 通过（2026-11-20）；真实 Docker Hub 发布未执行 |
| @DEP-S3 | 触发器断言仅 tag push；10 组非法版本执行检查；所有 run 步骤 Bash 语法检查 | 通过（2026-11-20） |
| @DEP-S4 | 发布凭据仅引用 docker 环境或 Repository secrets，缺失时在登录前退出 | 已检查配置；真实环境密钥与登录未验证（2026-11-20） |
| @DEP-S5 | Dockerfile 包含主应用与探针构建；dockerignore 排除私钥、本机配置及 `/configs/appsettings.json` | 静态检查通过（2026-11-20，2026-11-21 补充 `/configs/appsettings.json` 排除规则）；本机无 Docker，未执行镜像构建及容器验收 |
| 干净源码发布 | 与 Gitea 备份的文件清单对比；扫描 RSA 私钥、常见令牌形态、真实部署地址与私人邮箱 | 备份清单仅排除运行时私钥，另补发布验证文档；文本检查通过（2026-11-20），非完整安全审计 |
| @DEP-S6 / @DEP-S9 | 工作流矩阵/依赖/权限、版本解析 9 组、Bash 与 PowerShell 语法；Windows self-contained 最终包真实启动 | 通过（2026-11-20，SQLite/DuckDB、登录、首页、探针、校验和）；Linux/macOS 原生启动与实际 GitHub Release 上传待 CI，未打 tag |
| @DEP-S7 | `python -I tool/Release/test_release.py`：私密文件（含 configs/）拒绝、归档路径穿越、普通解压、包内无 configs 与启动脚本、appsettings 仅含控制台日志与无页面入口参数（默认值） | 通过（2026-11-20，test_release 6 项全部通过；2026-11-21 复跑通过）；首次启动及升级的人工场景见 bdd @DEP-S7 |
| @DEP-S8 | Release needs 仅 version/binaries，Docker job 独立 environment 与密钥；只有 publish-release 授 contents:write | 静态检查通过（2026-11-20），真实缺 Docker 密钥的 CI 路径未运行 |
| @DEP-S11 / @DEP-S12 / @DEP-S14 | `tests/SData.Ingest.Tests/ConfigsAppSettingsTests.cs`（xUnit，`dotnet test tests/SData.Ingest.Tests --filter ConfigsAppSettingsTests`）：目录不存在或为空时复制；已有覆盖配置不覆盖；默认配置缺失时不建目录 | 通过（2026-11-21，4/4；S11 对应 2 例、S12 对应 1 例、S14 对应 1 例） |
| @DEP-S11 | `tool/Release/smoke.py`：win-x64 单文件发布→`package.py`→smoke，首次启动后断言 `configs/appsettings.json` 与包内 `appsettings.json` 逐字节一致 | 通过（2026-11-21）；Linux/macOS 包未验证 |
| @DEP-S13 | 本机无 Docker：以容器同款布局运行发布构建（内容根为程序目录，空 `configs/`、`data/` 作为挂载点）。验证首次启动复制；覆盖配置改端口为 61234 后重启，服务监听 61234、默认 5300 未监听、覆盖文件保持不变；configs 无写权限时启动即抛出 `UnauthorizedAccessException` 并指出该文件 | 手工通过（2026-11-21）；真实镜像构建、容器内运行用户与 Linux 挂载权限未验证 |
| 发布前回归 | 前端 typecheck、vitest；MQ Producer 构建；S3 与 IoT 定向测试；工作流 YAML 与 Bash | 通过（2026-11-20，前端 748/748、S3 13/13、IoT 47/47，MQ 工具构建成功） |
