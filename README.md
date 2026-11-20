# SData

面向**内网单机部署**的高性能数据与观测平台：物联网数据接入、日志、链路、指标、数据分析与仪表盘。以 DuckDB 为分析引擎、SQLite 为元数据库，**零外部服务依赖**（不依赖 ES/PG/Redis/RabbitMQ/S3 等任何外部组件），小内存（默认 limit 512MB）即可运行。

## 架构

```
数据接入                    查询/分析                     存储
─────────                  ─────────                    ─────────
OTLP/HTTP(JSON) ┐                                        ┌ DuckDB（按天分表）
JSON 批量 API   ┼→ 内存缓冲 → 批量 Appender ─→           │  logs_/traces_/metrics_/iot_yyyymmdd
IoT 遥测        ┘                                        └ SQLite（元数据）
                                                    user/device/dashboard/saved_query
React 控制台 ←→ /api（JWT）←→ MediatR CQRS ←→ EF Core / DuckDB
```

- **日志/链路/指标/遥测** → DuckDB 按天分表，保留策略按天 DROP（秒级释放空间）。
- **元数据**（用户/设备/仪表盘/保存查询）→ SQLite，启动自动建库 + 增量修补 + 种子数据。
- **分析查询** → 只读受限执行器（仅单条 SELECT/WITH、参数绑定、行数上限）。

## 快速开始（开发）

```bash
# 后端（仓库根目录）
dotnet run --project src/SData
# 首次启动自动：建 SQLite + DuckDB、生成 RSA 密钥、seed admin

# 前端
cd ui && npm install && npm run dev   # http://localhost:4100，代理 /api → 5300

# 生产构建（输出到 src/SData/wwwroot，宿主同源托管）
cd ui && npm run build

# 发布（指定 RID 只带对应平台 DuckDB 原生库：350MB → 54MB）
dotnet publish src/SData/SData.csproj -c Release -r win-x64 --self-contained false
```

默认配置：端口 5300，admin 初始密码 `sdata@123`（来自 `configs/system.yaml`，登录后请修改），接入 API Key `sdata-dev-ingest-key`。

## 接入数据

```bash
# IoT 遥测（简单 JSON）
curl -X POST http://localhost:5300/api/ingest/iot \
  -H "X-Api-Key: sdata-dev-ingest-key" -H "Content-Type: application/json" \
  -d '[{"deviceKey":"dev-001","signal":"temperature","numValue":23.5}]'

# OTLP/HTTP(JSON) 日志（OpenTelemetry SDK 指向 http://localhost:5300/api/ingest/otlp/v1/logs，
# 协议配置为 http/json，并携带 X-Api-Key 头）
```

也支持 `/api/ingest/logs|traces|metrics`（简单 JSON 批量）。数据经内存缓冲批量落库，落库延迟约 200ms。

## API 一览

| 模块 | 端点 |
|---|---|
| 认证 | `POST /api/auth/login`、`GET /api/auth/me`、`POST /api/auth/password/change`、`GET/POST /api/users` |
| 接入 | `POST /api/ingest/otlp/v1/{logs,traces,metrics}`、`POST /api/ingest/{logs,traces,metrics,iot}`（X-Api-Key） |
| 日志 | `GET /api/logs`、`GET /api/logs/services` |
| 链路 | `GET /api/traces`、`GET /api/traces/{traceId}`（瀑布数据） |
| 指标 | `GET /api/metrics`、`POST /api/metrics/query` |
| 物联网 | `GET/POST /api/iot/devices`、`DELETE /api/iot/devices/{id}`、`GET /api/iot/devices/telemetry`、`GET /api/iot/devices/{key}/latest` |
| 分析 | `POST /api/analytics/query`、`GET/POST /api/analytics/queries`、`DELETE /api/analytics/queries/{id}` |
| 仪表盘 | `GET/POST /api/dashboards`、`GET /api/dashboards/{id}/data` |
| 监控 | `GET /health`、`GET /metrics`（Prometheus 文本）、`GET /api/monitor/status` |

开发模式暴露 OpenAPI（`GET /openapi/v1.json`，内置组件，生产环境关闭）。

## 依赖与体积

- 全部依赖为进程内组件（嵌入式 DuckDB/SQLite、进程内 MediatR/Serilog），**无任何外部服务**；唯一可选拨出口是 `SData:OTLP` 自身遥测导出（默认关闭，关闭时插桩不注册、零开销）。
- 发布产物约 54MB（指定 RID，含前端静态资源与 DuckDB 原生库），运行内存默认上限 512MB。

## 目录

```
src/            按域拆分的模块（infra/database/store + 9 个业务域）
ui/             React 19 + Vite + antd5 前端（构建产物内嵌 wwwroot）
docs/           文档标准与各模块四件套（sdd/bdd/tdd/sop）
tool/           SchemaSync（SQLite/DuckDB schema 管理）、I18nScanner
configs/        system.yaml（运行配置）
asserts/        生成的 schema 脚本
tests/          xUnit 测试（DuckDB 集成/SQLite/OTLP 解析）
deploy/         部署脚本与编排
```

## 贡献规范

- 后端：`AGENTS.md`（CQRS 分层、参数绑定、软删除、审计字段等硬规则）。
- 前端：`ui/AGENTS.md`（design-system 封装、i18n 双语同步、audit 列等）。
- 文档：`docs/DOC-STANDARD.md`（四件套 sdd/bdd/tdd/sop 与分层）。
