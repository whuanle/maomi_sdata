# 搜索告警设计
关联：[场景](bdd.md) · [验证](tdd.md) · [运维](sop.md) · [搜索](../analytics/sdd.md) · [通知](../settings/sdd.md) · [存储](../store/sdd.md)

## 边界
- Alert Shared/Core/Api 三层模块，跨域仅引用 Shared；宿主注入 AlertsCoreModule。
- SQLite 存规则、运行状态、7 天评估历史和通知 outbox；DuckDB 只读热数据。
- Search 共用 effectiveSearch 构建条件；快照包含 filters/denoise。规则条件冻结，修改搜索不自动修改规则。
- 日志/链路次数阈值支持 ≥/>/≤/<，1–60 分钟完整窗口、0–10 分钟延迟、1–10 连续窗口。
- 暂不支持分组、任意子查询、冷层回溯、仪表盘规则和周期重复提醒。

## 执行
- AlertConditionCompiler 拒绝表引用、子查询、注释、非白名单字段/函数；字面值重写为参数。
- IAnalyticsExecutor.ExecuteAlertCountAsync 强制半开时间窗，只取 count；阈值+1 截断计数标注下界。
- 不调用冷层解析/预热；读取本地台账检查已归档覆盖，超出热保留范围返回评估失败。
- 默认单槽、5 秒超时、15 秒/分钟预算，预览共用；默认启用规则上限 50。
- 原生 Cancel 中断专属连接，等待查询及中断回调退出才释放资源；不修改 DuckDB 全局线程配置。
- 调度仅最新完整窗口，不追赶积压；窗口缺口/错误重置连续命中，不误判恢复。

## 通知与权限
- 状态转换和 outbox 同事务；发送 worker 复用 INotificationSender，最多 3 次投递，默认待发送上限 1000。
- 至少一次语义：外部发送成功但本地提交前崩溃可能重复，不承诺严格 exactly-once。
- 规则版本更新/停用/删除使待发旧事件失效；已在网络发送中的请求无法撤回。
- admin/write 管理；readonly 可查看；渠道选择接口仅公开 id/name/type/enabled。
- 新 Id 字符串；保存搜索补充 ExactId，浏览器路由不转换为浮点数。

## 已知限制
- 本机 10 万行混合负载样本不能代表海量部署；限制扫描窗口并不等于限制扫描行数。
- 预算超限延后执行，不保证所有规则准点；共享 DuckDB 仍会有资源竞争。
- 预览为单窗口按需计数，趋势优先展示已有评估，不额外回扫历史。
