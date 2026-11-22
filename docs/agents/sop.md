# agents — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md) · [设置](../settings/sop.md)

1. 管理员打开系统设置 API & Agents，分别创建 Collector 接入 Key 和可信 Agent 访问 Key；“查看 Key”可再次显示并复制，关闭弹窗清除页面明文。列表不含秘密。
2. Collector 设置 X-Api-Key 或 Bearer；示例见 configs/otel-collector-example.yaml。浏览器 RUM 使用项目来源策略，禁止放入服务器秘密。
3. Agent 使用 Streamable HTTP `/api/mcp`，Header 为 `Authorization: Bearer <AGENT_KEY>`。页面提供 Claude Code、Cursor、VS Code、Codex 与通用配置。
4. “重置 Key”需确认，旧 Key 立即失效，请更新所有客户端；停用或删除影响后续请求，在途调用不强制取消。旧哈希 Key 无法还原，需先重置；密钥环损坏先恢复备份，否则只能重置，不自动改动原 Key。数据库与 SQLite 同目录 `notification-keys/` 必须共同备份并限制文件权限，密钥环文件未承诺静态加密。
5. 401 检查用途、启停和 Header；403 检查 Origin；429 缩减请求频率和并发。查询默认15分钟，最多24小时；截断时缩小时间窗。
6. `SData:Agents` 预算写在 `appsettings.json` 中：RequestsPerMinute 默认60（上限600）、PerKeyConcurrency 默认2（上限4）、GlobalConcurrency 默认8（上限16）、QueryTimeoutSeconds 默认10（上限10），修改后重启。
7. 不做 Host 白名单：服务监听所有网卡，访问地址由客户端决定；鉴权依赖 Agent Key（Bearer），并仅允许同源 Origin。反向代理需正确转发 Host 与 Origin，并部署 HTTPS/TLS。
8. 发现顺序：list_data_sources → get_data_organization → list_tables（offset续页）/describe_table → get_field_values。热表目录不代表冷历史；查询使用逻辑domain，不传物理表名。query_data用于明细、aggregate_data用于统计、compare_periods用于等长窗口对比（零基线变化率为空）。
9. 指标默认跨labels平均，可先list_metric_labels再过滤/分序列；服务发现另含traces分节。IoT先设备再信号；RUM先项目再summary，project是过滤而非授权范围。不要把span当请求、session当用户、Top N当总量或统计相关性当根因。
10. 原始属性不向 Agent 输出，文本常见秘密掩码不保证完整脱敏。Agent Key 可读取全局观测数据，仅授权可信服务；SQLite备份和文件权限仍需保护。
11. 截断时缩小时间窗、维度或投影，检查truncated/fieldTruncated；completeness=unknown不是完整数据证明，空结果不能证明无历史数据。目录翻页使用nextOffset。
12. 验收：initialize → tools/list（26个及只读/Schema）→ tools/call（发现、结构、明细、聚合、对比、IoT/RUM），复验凭据用途隔离/撤销、非法字段、输出预算和原生取消；证据见tdd。真实S3/反代/Agent客户端仍需部署验收，未部署不代表运行实例已启用。
