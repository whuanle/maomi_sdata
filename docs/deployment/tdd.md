# deployment — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| 单文件独立产物 + GC 配置 | `tool/Release/package.py`、`tool/Release/smoke.py`、`tool/Release/test_release.py`；`src/SData/runtimeconfig.template.json` | 2026-11-20：win-x64 单文件发布→打包→smoke 全通过（不依赖全局 runtime、SQLite/DuckDB、登录、首页、探针）；test_release 4/4。GC 配置确认并入 runtimeconfig 且模板不进包。实测体积：单文件 72MB / R2R+压缩 96MB（弃用），冷启动 2s vs 7s |
| @DEP-S1 / @DEP-S2 | 安全解析工作流 YAML，执行版本解析 Bash：稳定、预发布、构建元数据共 5 组 | 通过（2026-11-20）；真实 Docker Hub 发布未执行 |
| @DEP-S3 | 触发器断言仅 tag push；10 组非法版本执行检查；所有 run 步骤 Bash 语法检查 | 通过（2026-11-20） |
| @DEP-S4 | 发布凭据仅引用 docker 环境或 Repository secrets，缺失时在登录前退出 | 已检查配置；真实环境密钥与登录未验证（2026-11-20） |
| @DEP-S5 | Dockerfile 包含主应用与探针构建；dockerignore 排除私钥和本机配置 | 静态检查通过（2026-11-20）；本机无 Docker，未执行镜像构建及容器验收 |
| 干净源码发布 | 与 Gitea 备份的文件清单对比；扫描 RSA 私钥、常见令牌形态、真实部署地址与私人邮箱 | 备份清单仅排除运行时私钥，另补发布验证文档；文本检查通过（2026-11-20），非完整安全审计 |
| @DEP-S6 / @DEP-S9 | 工作流矩阵/依赖/权限、版本解析 9 组、Bash 与 PowerShell 语法；Windows self-contained 最终包真实启动 | 通过（2026-11-20，SQLite/DuckDB、登录、首页、探针、校验和）；Linux/macOS 原生启动与实际 GitHub Release 上传待 CI，未打 tag |
| @DEP-S7 | `python -I tool/Release/test_release.py`：私密文件拒绝、归档路径穿越、普通解压、首次配置密码转义与重启保留 | 通过（2026-11-20，4/4）；Windows 启动脚本另有最终包集成验收 |
| @DEP-S8 | Release needs 仅 version/binaries，Docker job 独立 environment 与密钥；只有 publish-release 授 contents:write | 静态检查通过（2026-11-20），真实缺 Docker 密钥的 CI 路径未运行 |
| 发布前回归 | 前端 typecheck、vitest；MQ Producer 构建；S3 与 IoT 定向测试；工作流 YAML 与 Bash | 通过（2026-11-20，前端 748/748、S3 13/13、IoT 47/47，MQ 工具构建成功） |
