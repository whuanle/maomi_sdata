# Maomi SData Go Migration Status

状态：**已完成第一阶段骨架（2026-11-21）**

## 已实现

- 独立 Go 模块：`go/`
- Chi HTTP 路由与显式依赖组装
- 配置加载：defaults < appsettings.json < configs/appsettings.json < 环境变量
- SQLite 元数据存储与启动 reconciliation
- PBKDF2 密码哈希兼容 C# 格式
- 管理员初始种子与摄取 API Key 种子
- JWT 登录、/me、用户管理、角色保护、密码修改
- DuckDB 1.5.6+ Go Driver 存储
- 有界摄取缓冲和后台 Appender 写入器
- 按四域摄取：logs/traces/metrics/iot
- `/health` 与 Prometheus `/metrics`
- logs/traces/metrics/iot 基础查询接口
- OpenAPI 切换不影响现有前端；静态 UI 回退支持 `go/web` 与 `ui/dist`
- Go Docker 与 `scripts/smoke-go.ps1`
- 登录、摄取、日志计数契约测试
- OTLP/HTTP JSON 与 protobuf 解析
- OTLP/gRPC Logs、Traces、Metrics 服务
- ES `_bulk` 与 `_index/_bulk` 基本兼容
- RUM beacon、config、probe 与查询占位路面
- Prometheus `/api/v1/query` 与 `/api/v1/query_range`
- 仪表盘、告警、保存查询的 SQLite CRUD
- MQTT 1883 监听与 `iot/<device>/<signal>` 发布摄取
- MQ 实例配置持久化与解析预览占位
- 指标 5m/1h rollup 表及维护周期骨架
- Kafka、RabbitMQ、Pulsar 消费管理器、配置热重载和 at-least-once 成功确认骨架
- 真实告警 tick 只读 SQL evaluation 与下次 attempt 更新
- RUM performance/session/error/replay/sitemap 基础聚合
- MCP tools/list 与工具调用健康/域计数
- Go API 公开路由契约清单与 C# controller 文件对照测试
- SQLite 再打开与用户行持久化集成测试
- Linux Go Docker 及同源 UI 容器构建准备
- C#/Go 双跑脚本和独立端口 health/metrics smoke

## 验证

- `go test -tags duckdb_use_lib ./...`：PASS
- 动态 DuckDB Windows 构建：PASS
- 管理员登录：PASS
- JWT `/api/auth/me`：PASS
- 摄取 API Key：PASS
- 100 条摄取批次落 DuckDB：PASS
- `/health`、`/metrics`：PASS
- smoke script：PASS
- OTLP/HTTP JSON、ES Bulk、Prometheus、RUM：PASS
- OTLP/gRPC 4317 TCP listener：PASS
- MQTT 1883 TCP listener：PASS
- 仪表盘、告警、保存查询 route smoke：PASS
- UI typecheck 与 Vite production build：PASS
- `/` 与 `/analytics` Go SPA fallback：PASS
- Go route contract 与 C# controller inventory test：PASS
- SQLite reopen/preserved-user integration：PASS
- Docker CLI/compose runtime：BLOCKED（docker.exe 未安装或未加入 PATH）
- 双跑检查：C# 与 Go `/health`、`/api/health`、`/metrics` 均为 200；`/api/overview/status` 均为未登录 401
- 登录双跑：若数据库已有不同初始密码，双跑可能返回不同状态；切换门禁应使用独立空数据库和相同初始密码
- 重启持久化 cutover gate：PASS，DuckDB 记录数由 1 保持为 1，`/` UI fallback 返回 200

## 尚未迁移

- OTLP protobuf 与 gRPC 完整实现
- ES Bulk 完整解析与 source 分片
- Kafka、RabbitMQ、Pulsar 消费器
- MQTT broker
- RUM 全量接口与 `/hub/rum`
- Dashboard、Alert、Settings 全量接口
- Agents/MCP
- 归档、保留、Rollup、冷表与词项索引
- 完整前端契约测试
- C#/Go 双跑与 Docker 生产切换
- Kafka、RabbitMQ、Pulsar 消费器的实际协议实现
- MQ 解析规则、断线重试和 at-least-once 确认
- MQTT 用户/设备密码 PBKDF2 与 ACL
- OTLP 事件属性、resource/labels 完整映射
- RUM replay、heatmap、sitemap 的完整聚合
- 冷层 S3/本地归档、词项索引、保留过期表
- 真实 Alert 计时、执行和通知投递
- 完整 MCP 工具集与 Agents 分析协议
- Docker/Linux 双架构与真实 15 分钟双跑压力未完成
- Docker CLI 在当前 Windows 主机不可用，容器构建与 compose runtime 未完成

## 当前约束

- 不修改现有 C# 工作区文件。
- Go 与 C# 暂时共存。
- 不删除或替换现有 Docker/C# 入口。
- 当前动态 DuckDB DLL 仅适用于本机 Windows 验证；Linux 构建在 Go Docker 中使用官方预编译 CGO 链接。
- Docker 运行时验证由外部 Docker 主机执行；本机缺少 Docker CLI，不视为已通过。
