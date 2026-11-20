# logs — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md)

## 目标

对已接入日志的低延迟检索：服务/级别过滤 + 正文关键字，时间倒序分页。

## 组件

- `QueryLogsCommand`（分页 + 过滤）→ `QueryLogsCommandHandler` → `ILogStore.QueryAsync`。
- `QueryLogServicesCommand` → 最近 3 个天表的 DISTINCT service。

## 决策

- 详情消息体由 `LogBodyPreview` 展示：默认最高 min(8rem,20vh)，实际溢出才显示展开/收起，展开后最高 50vh 并内部滚动。长行强制换行、容器 min-w-0/max-w-full，复制保留全文；ResizeObserver 随容器宽度变化重测。

- 默认窗口：未指定时间范围时查最近 1 小时（防止全表扫）；级别过滤为参数化 IN；关键字为 `body LIKE %kw%`（参数绑定）。
- 分页 count 返回 -1（避免大区间 count(*)），前端以「最多 N 条」模式呈现。
- 上下文共用 logs/traces 查询构建器，支持同主机与嵌套标量属性，最多 24 个 AND 条件；固定微秒锚点。查看关联记录打开详情但不移动锚点，显式重新定位才重查；按记录身份而非仅时间高亮。
- 详情上下文经 `IAnalyticsExecutor` 分批查询：固定时间窗与匹配条件，每批展示 100 条，SQL 多查一行、`rowLimit=100`，以 `truncated` 判断续页；分页 limit/offset 参数绑定，日志 ID 转字符串避免 JS 精度丢失。
- 按 ts、id（NULL 在后）及展示列确定排序，使用 OFFSET 兼容历史 NULL/重复 ID；不按 ID 过滤历史数据。跨页没有数据库快照，并发补写或保留清理可能造成位置移动、重复或遗漏，需重新查询刷新；深页 OFFSET 扫描成本会增加。
- IntersectionObserver 监听 Drawer 内容滚动容器；请求锁防重复触底，代次隔离旧响应，续页失败保留列表与 offset 供重试。全部结束后展示终态，不再续载。
- 统一 JSON 字段引用（`ui/src/pages/search/jsonFields.ts`）：字段以「根列 + key/索引段」保存，显示形态 `attributes["http"]["status_code"]`（括号嵌套）与 legacy 点号 `attributes.http`（单层字面 key，与后端 QueryDslCompiler 一致）并存互不串查；查询一律编译为 `json_valid` 防护 + 绑定路径（`$jpN`）的 `json_extract_string`，数组仅显式索引。JSON 字段可作 SELECT 结果列（以显示名作带引号列别名）与过滤器/排序引用，Lucene/SQL 两模式同构；`body` 为文本列，JSON 化文档（ES Bulk 等）经 json_valid 防护安全参与发现与过滤。
- JSON 属性发现不设后端元数据：SELECT 浮层与过滤器树复用 facet 通道（`/analytics/search` 直查）逐层懒加载 `json_extract(绑定路径) → json_keys` 枚举子属性，锚定当前搜索时间窗、抽样估计口径（默认 USING SAMPLE 1000 行，搜索态全窗扫描），根白名单 logs=attributes/resource/body、traces=attributes/resource/events；发现结果标注采样口径，不宣称完整 schema。
- WHERE 输入框智能建议（`WhereSuggestInput`）：光标上下文由 `resolveTokenContext` 解析（反向词元扫描 + 方括号深度感知冒号切分，与编译器词法同源；引号短语进行中退化为字段建议，仅影响提示质量不影响编译）。Lucene 字段候选 = JSON 根 + 物理列（括号路径形态，点号 legacy 形态只保编译兼容不进建议）；值候选复用 `buildFieldValuesSql`（facet 同链路、防抖 200ms），SQL 模式仅静态列/关键字。弹层打开时 Enter/Tab 采纳候选并截断查询执行，IME 组合输入期间不拦截任何键；建议查询不随 filters 变化重查（与 facet 同口径避免反馈环）。
