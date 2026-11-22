# 文档导航

## 全局规范（L1）

- [功能架构总览](architecture.md) — 摄取协议矩阵/存储分层/RUM/查询协议与里程碑
- [页面功能模块](ui-pages.md) — 菜单/页面/读写权限设计（对标 ClickStack、OpenObserve）
- [并行实施计划](plan/README.md) — 多会话协作协议 + [任务卡](plan/TASKS.md) + [状态账本](plan/STATE.md)
- [文档标准](DOC-STANDARD.md) — 分层与四件套契约
- [CQRS 与分层约定](cqrs-conventions.md) — 后端编码标准
- [存储布局](storage-layout.md) — SQLite 元数据 + DuckDB 按天分表
- 前端规范见 `ui/AGENTS.md`

## 模块文档（L2，四件套）

| 模块 | 文档 | 说明 |
|---|---|---|
| store（DuckDB 引擎） | [store/](store/) | 连接管理/按天分表/缓冲落库/保留策略 |
| store · 性能专项 | [performance-audit](store/performance-audit.md) | 写入/查询/维护任务的性能审计与优化方案（PF 任务组，立项未实施） |
| ingest（数据接入） | [ingest/](ingest/) | OTLP/HTTP(JSON) + 简单 JSON 批量 + API Key |
| logs | [logs/](logs/) | 日志查询 |
| agents | [agents/](agents/) | API & Agents 双用途凭据、只读 MCP 与查询预算 |
| analytics | [analytics/](analytics/) | 只读 SQL 执行 + 保存查询 |
| alerts | [alerts/](alerts/) | Search 次数告警、后台资源预算与通知 outbox |
| dashboard | [dashboard/](dashboard/) | 仪表盘配置与面板数据 |
| auth | [auth/](auth/) | 本地账号 + JWT(RSA) + 接入 Key |
| traces / metrics / iot / monitor / rum | [traces/](traces/) · [metrics/](metrics/) · [iot/](iot/) · [monitor/](monitor/) · [rum/](rum/) | 四件套齐（T900 收尾）：metrics 含 PromQL 子集与 Prometheus 兼容 API（configs/grafana-note.md）；monitor 含总览聚合；rum 含 SDK/回放/热力 |
| settings（系统设置） | [settings/](settings/) | 运行时设置通道（SQLite 覆盖 + 文件回退）：S3 冷存储/三段保留/压缩立即生效，MQ/OTLP 重启生效，整页管理员 |
| ingest 扩展通道 | [ingest/](ingest/) | OTLP 标准路径(/v1/*)+gRPC+ES Bulk+MQTT+RUM beacon（各 bdd/tdd） |

## 回归入口

```bash
dotnet test tests/SData.Store.Tests       # DuckDB 全链路（@auto:xunit）
dotnet test tests/SData.Database.Tests    # SQLite 审计/软删除/schema 自愈
dotnet test tests/SData.Ingest.Tests      # OTLP 解析 + 缓冲
```
