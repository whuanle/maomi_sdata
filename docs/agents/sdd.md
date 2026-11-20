# agents — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · [设置](../settings/sdd.md)

## 边界

- 官方 .NET MCP SDK，Streamable HTTP `/api/mcp`，无状态；每个请求重新校验独立 Agent Key。
- SQLite `agent_key` 保存 SHA256、前缀、名称、启停、审计及受保护秘密。管理员可显式查看/重置，MCP 和列表不返回秘密。Data Protection 使用独立 purpose 并绑定用途/记录ID；旧哈希 Key 需先重置，详见 [设置设计](../settings/sdd.md)。
- 26个显式只读工具，保留原8个名称；发现/通用查询/域分析由 AgentAnalysis 承载，旧调用保留列式 rows。工具清单和参数以 MCP tools/list 为准，不接受 SQL，不开放写操作或敏感元数据。
- list_data_sources/get_data_schema 描述六类逻辑源、字段类型/单位/行语义；list_tables/describe_table 经 IAnalyticsCatalog 读取 main 下允许日表实际结构，过滤内部表/冻结表/回放；字段结构不等于读取权限。目录分页，不扫描业务行；不枚举冷文件。
- get_data_organization 描述 UTC 日分区、[from,to)、冷热透明、关联键与有效保留计划，不含路径。get_field_values 仅枚举允许维度 Top N。
- query_data/aggregate_data/compare_periods 用结构化白名单编译固定操作，经 IAnalyticsExecutor 参数绑定、ITableResolver 和专属可取消连接查询；不开放 SQL/任意 JSON 路径。RUM 事件复用 {{rum_events}}。
- 新请求实现 IModelValidator，MCP 入口显式验证；Infra 注册器优先匹配类型自身的验证接口，避免继承模型漏注册。默认15分钟、每窗口最多24小时、最多200行/组；对比窗口等长且共享单次截止时间。
- 服务发现保留日志 rows 并增加 traces 分节。指标默认跨 labels 平均，可按8个安全标签过滤/分序列，最多20序列/2000点；不计算 counter rate。IoT 提供设备/信号发现、数值桶与质量分布。
- 汇总仅返回事实：日志趋势、span/去重 trace/耗时分位数、父子 span 服务依赖（按标识去重，排除歧义）；RUM 按项目统计页面上报、distinct session、Vitals p75与错误。会话不是用户，分组 trace 数不可求和，有限依赖不代表完整拓扑。
- 每 Key 默认每分钟60请求、2并发，全局8并发；可在 SData:Agents 调低或在硬上限内调整。单次查询最多10秒并中断原生工作。
- 固定 SQL 限制行数；新工具完整服务结果预算32KiB（为SDK文本/结构双包装留余量），旧工具行预算28KiB；超额标记 truncated，字段截短单列 fieldTruncated，当前不抽样。raw attributes/resource/events/payload/replay 不输出；常见秘密掩码非完整DLP。返回 completeness=unknown，不承诺冷层完整性；目录过大可丢弃 metadata 并标记截断。
- Host 仅 Server 主机、loopback及 Agents.AllowedHosts；Origin仅同源。MCP请求体最多64KiB，不开放跨域。

## 局限

- 只读 Agent 获得全局观测数据读取权限，无租户/服务级隔离；仅交给可信 Agent。
- 凭据撤销阻止后续请求，不强制取消已授权的在途调用。HTTPS和网络边界由部署提供。
