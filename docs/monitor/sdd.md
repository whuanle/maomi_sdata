# monitor — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md)

## 目标

服务自身状态（/health、/metrics、/api/monitor/status）与总览页聚合（/api/overview/status）。AGENTS.md 第 5 节：服务状态只经 Monitor 模块，各模块不得自造状态接口。

## 组件

- `MonitorController`（Api）：/health 与 /api/health（匿名、同一存活探针，后者兼容前端统一客户端）、/metrics（Prometheus 文本，匿名）、/api/monitor/status（登录）、/api/overview/status（登录，T314）。/metrics 暴露 `sdata_gc_heap_bytes` 与 `sdata_gc_collection_count_total{generation=0|1|2}`（进程启动以来累计回收次数，配合 Prometheus rate 观察 GC 调优效果）；数据源为 ResourceSampler 的 5 秒快照与 StatusResponse。
- `QueryStatusCommandHandler`：进程指标 + 缓冲统计 + 四域今日行数 + `mqConsumers`。MQ 快照经 Ingest.Shared 跨域契约读取，包含实例 ID、状态、进程累计落库数、最后落库时间与脱敏错误；无凭据，无额外状态端点。
- `QueryOverviewCommandHandler`（T314）：七个数据类型域（logs/traces/metrics/iot/rum_event/rum_session/rum_replay）的今日行数、天表数、通道健康（缓冲统计按 IngestKind 小写键映射：log/trace/metric/telemetry/rumevent/rumsession/rumreplay），存储用量（热层 = AppConst.GetPath() + DuckDb:Path 的 FileInfo.Length；S3 标志读取 Settings.Shared 有效配置，不表示已连接）。合并配置实例与 MqRuntime 快照，输出不含连接参数及原始异常的 MQ 摘要，并单独返回共享队列长度。

## 数据布局

消费历史由 Store.Shared 的 `IMqConsumptionStore` 查询 DuckDB 日表，`/api/monitor/mq-consumption` 仅管理员可访问，历史范围最多七天、返回最多一万条并标记截断；实时与历史分开返回。其余数据量由 IIngestBuffer/IStoreInfo 聚合，资源由后台快照提供：ResourceSampler 每 5 秒采样系统与进程，Store.Shared 的 IStorageMetrics 提供本地存储与 S3 快照。GET 不触发远端列举。

## 关键决策

- 通道健康按数据类型维度而非接入协议维度：缓冲统计仅有 kind 粒度（http-json/otlp/esbulk 共享 log/trace/metric kind），协议级区分需按端点埋点，留待需要时演进。
- 今日行数批量统计：`QueryStatusCommandHandler` 与 `QueryOverviewCommandHandler` 经 `IStoreInfo.CountTablesAsync` 把各域当日表合并为一条 `UNION ALL` 查询，单次查询门获取 + 单次连接克隆取代逐域 count；未知/不存在表按 0 计，输出与逐表统计一致。
- 存储探测直接读数据库文件大小（Monitor.Core 经传递引用 AppConst），不给 IStoreInfo 增加契约面。
- 前端 api client 统一 `/api` 前缀，与全局 ApiRouteConvention("api") 对齐；overview 端点用 action 级绝对路由 `/api/overview/status`。
- Overview 仅轮询 Monitor 接口，每 10 秒刷新；移除日志/链路样本查询，今日总量采用七域 UTC 数据日期口径，落库/丢弃为进程累计。共享队列长度不按类型累加。
- ResourceSampler 用 Process/Stopwatch 差分计算进程 CPU；Windows 用 GetSystemTimes/GlobalMemoryStatusEx，Linux 用 /proc/stat、/proc/meminfo，当前进程的 cgroup 挂载提供容器配额；macOS 用 Mach host_statistics/host_statistics64 与 sysctl hw.memsize，运行时读取页大小（兼容 4KB/16KB），可用内存按 free + inactive 估计，不等同于活动监视器口径。Mach host 端口引用每次采样后释放。系统可见资源不等于物理宿主，首次 CPU 差分前为空。
- S3 配置标志与最近列举状态分开；只读列举统计通过 Store.Shared 快照获取，范围、失败与旧值口径见 [store 设计](../store/sdd.md)。不调用写对象测试。MQ 空闲/历史落库不推断实时连通性。

- 日志消费 Status 与协议详情复用配置规则级运行快照，以名称和匹配方式定位各自状态、错误和速率；前缀规则只汇总并展示匹配数量，不展开实际主题。速率可用性按各规则状态判断，刷新失败隐藏旧速率；消息/记录最多两位小数、字节速率最多一位并去尾零，极小正值用精度下限表示。累计值与后端窗口不变。未恢复失败优先显示消费失败，不再显示正在消费；恢复后的失败只保留诊断历史。自动重试显示等待重试、次数、执行时间与倒计时，实例立即重试经管理员确认后由管理器串行重建。

- Status 阶段诊断与查询故障分离：请求预算 8 秒，区分 HTTP/响应解析/契约错误，保留失败时间和重试；消费显示阻塞消息数、最早阻塞时间、持久化等待、待确认及各阶段最近失败。解析失败不再入库保底或跳过，完整解析与提交前不允许确认。共享 writer 失败由缓冲安全快照返回，区分内存不足、数据库失效和磁盘错误；成功后记录恢复时间，Status 完全隐藏已恢复的共享写入故障，仅未恢复故障显示告警，不返回异常原文。

- 消费重试与通知状态继续由Monitor暴露；通知失败与消费失败分开，冷却截止和抑制次数来自Ingest安全镜像。重试操作仅管理员可用，不修改Enabled或已确认消息。

## 已知问题

- Prometheus 文本与 /metrics 端点名与 OTLP /v1/metrics 无冲突（路径不同）。
- 通道健康"健康"定义暂为 dropped == 0，比率阈值待实际运行数据校准。
