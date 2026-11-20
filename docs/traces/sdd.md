# traces — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md) · [ingest](../ingest/sdd.md) · 证据：[侧栏测试](../../ui/src/pages/search/__tests__/TraceDetailDrawer.test.tsx)

## 目标

统一搜索中的链路专属筛选、完整 span 行展开，以及时间轴与属性联动的宽侧栏。

## 组件与数据

- 后端保留 TracesController 列表/详情与 SearchTracesCommand DSL；搜索 UI 通过 analytics/search 的只读执行器查询。
- SearchSidePanel 按 traces 展示属性、名称、服务、类型、状态、根 span 和毫秒耗时范围；数值换算为微秒并参数绑定。
- SearchPage 查询额外保留隐藏身份列，显示与 CSV 导出仍按用户选择列。
- SpanPreview 共用概览与列值树，按 trace_id + span_id 补查完整记录。
- TraceDetailDrawer 固定打开时的搜索时间窗；traceSearch 复用冷热层解析并返回截断信息。
- TraceWaterfall 的概览滑块与主时间视窗联动，支持放大/缩小、Ctrl/Cmd+滚轮缩放和复位；父子树与图表共用最多 18 个可视行，大链路通过滚轮或行位置滑块浏览，避免超高画布。服务分色、错误标红，条尾重复「自适应 µs/ms/s 耗时 · span 名称」，与左树和详情选中联动；预留 28% 尾部空间，按画布剩余宽度单行省略，优先保留耗时，右侧裁剪用 → 标记继续，完整服务/名称/耗时保留 tooltip。异常时间不参与定位。
- 日志与事件独立开关、提示与点击：日志打开详情，事件展示名称/时间/属性；未映射日志有独立行，折叠数量、日志截断与加载错误显式提示。图表范围包含关联标记，但头部链路跨度仅按 spans 的最早开始至最晚结束计算，不累加嵌套耗时。
- Trace 顶层 Surrounding Context 复用日志上下文状态机，默认周边 traces，可切 logs；默认 ±30s、8 档窗口，Anything/Service/Host/Custom 匹配。结果域采用独立静态列/排序与分页，切域清空匹配条件，值与 JSON 路径均绑定。
- 上下文支持 attributes/resource 嵌套标量叶子，最多 24 个 AND 条件；缺少 host.name 明确提示。查看记录不移动锚点，显式重新定位才改变窗口；微秒锚点不经 Date/Dayjs 毫秒截断。日志按字符串 ID、span 按 trace/span/时间定位；旧响应隔离与失败重试保持原分页机制。
- ServiceMapTab 消费同一份 spans，不重复请求；关联日志继续复用日志详情及上下文。
- OTLP traces 保留微秒时间和耗时，映射 ok/error/unset；protobuf 事件属性写入命名对象。

## 独立服务地图（2026-11-19）

- `/service-map` 复用 analytics/search、`{{traces}}` 冷热解析和命名参数绑定，不新增表或图形库；全登录角色可查。
- 工具栏服务选择宽 192px、语法选择宽 96px，搜索框弹性占剩余空间；宽屏同排，窄屏分查询与执行两组换行；指标选择宽 192px，不继承表单控件默认整行宽度。
- 页面继承宿主可用高度，时间轴固定 100px，画布弹性占满剩余高度并保留 360px 最小高度；宽屏详情与画布等高且内部滚动，窄屏详情置于图下并限制高度。ECharts 由 ResizeObserver 随容器调整，不再固定 480px。
- 服务和 SQL WHERE/Lucene 条件先匹配 Trace 集合，再按 trace_id 稳定哈希采样（默认 100%），保留窗口内全部有效身份片段；不外推全量。
- `(trace_id, span_id)` 去重，按时间、服务、父标识、名称、耗时、状态和类型确定保留记录；父子关联同时约束 trace_id 与 span_id。同服务调用不成跨服务边。
- 节点统计 span/Trace 数、错误和平均片段耗时；边统计跨服务父子调用。根与缺父独立计数，窗口为 `[from,to)`，不推测窗口外关系。
- 完成聚合后保留最活跃 100 个节点、节点间 300 条边；元信息报告原始聚合数量与裁剪。服务候选前 200 项、真实 Trace 样例前 20 条。
- 时间桶统计桶内去重样本 Trace（可跨桶重复），点击缩窗重查；执行快照与 generation 防旧响应覆盖。URL 恢复条件与时间。
- ECharts 固定分层布局以强连通分量保留回环；节点可聚焦上下游，边钻取真实 Trace，顺序与并发通过既有瀑布图查看。聚合路径不等于真实路径，平均 span 耗时不等于端到端延迟。
- 服务名 tooltip 使用 richText 而非 HTML；时间序列化显式 UTC。查询输出上限不是扫描成本预算，大窗口仍需调低采样或缩小范围。

## 边界

- 展示的是查询窗口内链路，不保证窗口外 span 完整；缺父节点、截断均提示。
- span 上限 10000，关联日志上限 200；facet 为时间窗抽样计数。
- 不新增 links/scope 存储字段，不伪造未采集属性；历史已截断的时间精度无法恢复。
