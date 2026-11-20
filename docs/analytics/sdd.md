# analytics — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/sdd.md) · 证据：`tests/SData.Store.Tests.Analytics_RejectsWrite_AllowsSelect`

## Analytics 工作台

- `GET /api/analytics/tables` 经认证与 `IAnalyticsCatalog` 读取实时 DuckDB 表/字段类型/可空性，不扫描业务行，不列 SQLite 元数据。
- 左侧目录与表结构平分标题栏下剩余高度，分别固定搜索框并独立滚动；表名/字段名大小写不敏感筛选，切表清空字段筛选。右侧 SQL/Lucene 与结果；结构/示例不覆盖草稿，字段/表名显式光标插入。CodeMirror 实际 schema 补全，物理表与逻辑占位符分离。
- 查询栏采用紧凑双行：首行目录/来源和保存操作，次行固定宽度模式选择、可伸缩 Lucene 输入及右侧时间/行数/执行；SQL 保留独立多行编辑器，窄屏允许换行。
- Lucene 风格子集支持五类逻辑来源、AND/OR/NOT、括号、字段/短语/比较/前缀；条件与时间经搜索执行器绑定，不承诺全文索引或完整 Lucene。
- `kind=analytics` 版本化 Definition 保存模式/来源/双草稿/参数/行数/相对窗口，后端校验结构。载入不执行，旧 sql 兼容，search 快照不混列。SQL 时间条件须显式写入。

## 搜索告警

- Search 提供 [Alerts](../alerts/sdd.md) 入口；共享有效条件构建器，保存快照补齐 filters/denoise，规则冻结条件且不随搜索编辑静默变化。
- 保存搜索 ExactId 无损字符串用于关联与路由；存在关联告警时须先删除告警再删除保存搜索。

## 目标

把 DuckDB 的分析能力安全地开放给用户：只读 SQL 控制台 + 保存查询（仪表盘面板复用同一执行通道）。

## 组件

- 侧边菜单文案统一读取双语语言包：简体中文显示「仪表盘」「客户端会话」，英语显示「Dashboards」「Client Sessions」；语言切换即时更新，不改变菜单顺序与路由。

- `ExecuteQueryCommand` → `IAnalyticsExecutor`（单语句 SELECT/WITH 校验、写关键字黑名单、行上限 ≤10000、独立查询连接；只读由现有 SQL 校验约束，非数据库级沙箱）。
- `ExecuteSearchSqlCommand`（`POST /api/analytics/search`）→ `IAnalyticsExecutor.ExecuteSearchAsync`：统一搜索 SQL 直查——`{{logs}}/{{traces}}/{{metrics}}/{{iot}}` 表占位符由 `SearchSqlExpander` 在字符串字面量之外展开为区间内现存按天表 UNION ALL（表名只来自 TimeTables 前缀 + 内部日期）；`$start`/`$end` 以 DuckDBParameter 绑定（值来自请求时间范围）；请求可选 `Parameters` 字典绑定其余 `$name` 引用（键小写字母开头 ≤64 个，值仅字符串/数字/布尔/null，全部 DuckDBParameter 绑定、被引用但缺失时 400）；跨度受 MaxQueryDays 约束；区间内占位符无现存天表时短路返回空结果；其余约束同 ExecuteAsync。逻辑来源经 ITableResolver 合并热表与可用冷文件；占位符本身不自动注入行级时间条件。
- `SaveQuery/QuerySavedQueries/GetSavedQuery/DeleteSavedQuery/ExecuteSavedQuery`：保存查询 CRUD + 按 id 查单条（SQLite），保存前先以 `SELECT * FROM (sql) LIMIT 0` 验证合法性。
- `SavedQueryEntity.Kind`（`sql`/`search`）：统一搜索页（`/search`）的「保存搜索」复用同一实体——`kind=search` 存 `Definition` JSON（domain/mode/where/fields/orderBy/limit/rangeMs；历史 sql/query 格式仍可打开）而不存 SQL，不经验证器；列表支持按 Kind 过滤；执行通道对 `kind=search` 返回 400（无 SQL 可执行）。
- 统一搜索页（`/search`）HyperDX 式双模式：默认 Lucene 属性搜索（前端 `ui/src/pages/search/lucene.ts` 编译为参数化 WHERE 片段拼入搜索 SQL，`field:value`/数值比较/前缀 `*`/`-`排除/裸词全文/OR/括号/JSON 路径 `json_extract_string`，字段与排序输入按表结构白名单校验），语法切换回 SQL 编辑器（CodeMirror 直写）；过滤器面板与列头/明细抽屉联动生成过滤条件，全部并入同一参数化查询。
- 图表探索页（`/charts`，2026-11-18 Time Series 对齐 HyperDX）：图表/样本/生成 SQL 以「执行快照」（chartType/domain/range/series）渲染——编辑条件不重解释旧结果，旧请求经 requestId 丢弃；相对预设每次运行以 now 重锚定（区间固定 ISO 由快照持有），自定义区间固定不变，自动粒度按实际跨度计算；时间选择复用 `TimeRangePicker`（隐藏 Live Tail，绝对区间直显触发器）；样本事件复用搜索页 `ResultsTable`（箭头就地展开/行点击开 `LogDetailDrawer`/trace 开 `TraceDetailDrawer`），样本列包含 id/span_id/attributes 等身份与详情列以支撑抽屉补查；「按值过滤」经 `buildFilterConditionSql` 生成 `$pN` 参数绑定条件并入序列 WHERE（SQL 模式沿用其参数字典），样本查询失败经 `Promise.allSettled` 显式报错不掩盖图表；生成 SQL 与预览/复制经 `displaySql.ts` 展示实际绑定值（字符串正确转义，保留 $start/$end），不再展示参数字典；展示产物禁止回流执行或保存，原始 SQL 与参数仍分离；数据源与运行区操作菜单改为「预览 SQL」，打开 Modal 并冻结当前 SQL/参数快照，预览后显式点击复制才写入剪贴板，不执行查询。渲染形态始终跟随激活页签（2026-11-18 修复页签数据互串）：执行快照只承担缩放/下钻回填与「最近一次执行」记录，不再决定渲染类型；切换共享查询页签（时序图/数值）立即清掉上一页签的结果与错误并按新类型重查；表格/柱状图/饼图/事件搜索/热力图/模式页签由各自探索器独立承载，不共享页面级查询状态。
- 统一搜索页 URL 条件同步（`ui/src/pages/search/searchUrlState.ts` 纯函数，bdd S15/S16）：条件状态单向投影到地址栏——首挂载 `parseSearchUrlState` 从 query 恢复全部条件（`source/mode/q/sql/fields/orderBy/filters/denoise/range|from+to/limit/refresh`，非法值安全回落默认），变更经 effect `serializeSearchUrlState` 以 replace 写回（非默认才写入，等值守卫防回环）；`savedId` 上下文禁用同步。

- Chart Builder 支持 logs/metrics/sessions/traces；Sessions 使用 RUM 会话天表，窗口内按 project_key/session_id 去重，ts 为窗口内首次上报；样本为各序列条件的并集。时间跨度联动显示实际 Auto 粒度；缩放同步时间与样本。Explorer 与仪表盘复用 `chartOption.ts` 渲染长表分组结果，保存链路见 [dashboard](../dashboard/sdd.md)。

## 共享图表工作区（2026-11-19）

- `ChartExplorerPage` 仅管理路由和 URL；独立探索与 Dashboard 共用无路由依赖的 `ChartWorkspace`。执行快照、公式编译、样本查询及参数绑定保持原通道；panel 模式显式运行、只应用草稿且不写探索地址。面板查询、展示和编辑恢复的独立协议及旧配置兼容见 [dashboard](../dashboard/sdd.md)。

## 图表链接状态

- `/charts?tab=...&state=...` 由 `chartUrlState.ts` 保存版本化原始查询定义、时间、SQL 及绑定参数；八页签仅激活项经工作区上下文上报草稿，统一 replace 写回，不改变编辑后显式运行的行为。相对时间仅存预设，绝对范围固定；序列标识、公式、结构化筛选/排序、指标条件、分类上限和热力图分桶保持一致。不保存结果、分页位置或弹窗。
- 首查前整体校验并恢复，非法状态回落当前页签默认条件；外部导航重建探索器，自身写回不重建，避免重复首查和隐藏页签覆盖。仪表盘编辑保留面板上下文，不读写探索 URL。链接含搜索值，分享前须检查敏感内容；复杂条件可能产生长链接。

## 搜索语法说明

- `LuceneSearchHelp` 在搜索输入框下默认展开，可收起；当前条件明确为编辑草稿，时间范围、过滤器和降噪仍生效。双语示例仅展示，不填入或执行，不改变共享智能提示组件。
- 示例只覆盖当前编译器支持的子集：裸词/短语子串、字段等值、末尾星号前缀、负号排除、空格连接和 OR；存在性指引侧栏，不宣称支持通用通配符；编译器现已支持显式 AND/NOT。说明不写入 URL 或保存快照，SQL 模式隐藏。

- 图表复用 `LuceneSearchHelp` 的 chart 场景：时序/数值按序列、柱状/饼图/事件搜索/热力图/模式按当前条件展示，仅实际 Lucene 输入可见。四来源示例使用各自字段，图表文案指引 Run 生效及 SQL 存在性判断，不沿用搜索侧栏/降噪说明；不扩展各页签语言支持，仪表盘编辑自动复用。

## 时序图 SQL 工作区

- Time Series SQL 模式复用 CodeMirror（260px、Ctrl/Cmd+Enter），上方默认展开 `TimeSeriesSqlInstructions`，说明 `ts_bucket/value/series` 长表契约及 DuckDB 示例；示例仅复制，不自动替换或运行草稿。
- 数据源行承载告警与显示设置；编辑器下方时间/运行工具条左对齐。隐藏 Builder 序列、公式及不生效的粒度控件；切源同步 SQL 编辑器和来源示例，各来源草稿与参数独立缓存，首次进入使用该来源默认聚合；Builder 首次转 SQL 编译当前配置而非旧执行结果。切源清空旧结果并隔离迟到响应，点击 Run 后才提交，不跨来源重写用户草稿。

## 表格页签

- 默认最近24小时：日志/链路按 service 计数、指标按 name 求 avg(value)、会话按 project_key 计数；首次激活与切源自动查询，StrictMode 使用可取消微任务首查。切源同步失效主查询和下钻、清空结果/详情/保存，SQL 草稿按来源隔离。
- `tableBuilder.ts` 复用 SEARCH_FIELDS 提供类型约束、结构化筛选及聚合阈值，所有值共用绑定参数空间；下钻和保存保留编译后 SQL/参数。`TableFilterEditor` 聚焦时按当前范围取真实值，防抖、键盘采纳、IME/失败手填与过期响应隔离。
- 分组标签和聚合字段选择替代盲填，常用默认/错误/级别/p95预设一键运行；高级 WHERE 复用 WhereSuggestInput，公式提供引用和百分比模板，SqlEditor 可选 schema 补全。未运行草稿标识、执行来源/绝对时间、无数据恢复和7天扩窗明确呈现。

- `TableExplorer` 独立持有草稿与执行快照，首次激活运行、切回保留配置；无时间桶或粒度。多聚合、公式、WHERE/HAVING、共享分组与结构化多列排序。
- `tableQuery.ts` 按完整分组元组对齐宽列；分位数 p50/p90/p95/p99、公式常量与 Top N 参数绑定。完整聚合与公式之后排序，维度稳定打破平局，NULL 最后；默认 Top 10、范围 1–500，N+1 探测更多分组。
- 指标以稳定 ID 关联排序和逐列设置；别名冲突消歧。Builder 表头排序按已执行条件和绝对时间重查 Top N；raw SQL 只对已返回结果排序，最多 500 行。
- `ChartTable`/`TableSettings` 与 Dashboard 共用格式、语义色、斑马纹、可选吸顶、滚动与 CSV；NUL 已修复。分组下钻仅查样本，不改聚合结果，四来源字段校验及值绑定、请求代次丢弃旧样本。
- 查询仍经统一 `IAnalyticsExecutor`，WHERE/HAVING 为用户分析 SQL 表达式，不另开执行通道。浏览器亮暗/窄屏/保存重开验收被登录阻断，未宣称完成。

## 公共匹配样本

- `SampleMatchedEvents` 供 Time Series、Bar、Pie 与 Heatmap 共用折叠区、数量、状态、搜索表格及详情；日志/链路沿用 ResultsTable 和详情抽屉，指标/会话展示领域数据与项目内会话回放。新查询复位详情，隐藏页签卸载浮层，折叠区 ID 唯一。
- 查询仍由父 Explorer 管理；Bar 样本独立记录提交来源/模式/绝对时间，图表失败不污染成功样本。按值过滤恢复执行快照，经 `compileChartWhere` 绑定参数，支持仅此值及删除过滤；过滤存入 editor.valueFilters，保存重开可恢复。

## 类别柱状图

- `BarExplorer` 独立单序列草稿及执行快照；无公式/复制/时间粒度。四来源 WHERE 支持 Lucene/SQL 下拉切换与独立草稿；复用 `WhereSuggestInput` 的字段/JSON/值建议及 SQL 字段/关键字建议，Enter/Tab 采纳不运行。Lucene 值与 SQL 条件字面量均参数绑定，SQL 条件拒绝注释/多语句；数值字段及聚合白名单校验，支持 p95。
- 非 Count 聚合显示可输入的 `SQL Column` 并提供字段提示，切换 Count 隐藏但保留草稿；`barColumn.ts` 复用受限算术 AST，支持字段、绑定 JSON 路径和四则运算，常量参数绑定、除零保持空值，不开放任意函数或子查询。
- `barQuery.ts` 在完整窗口按分类元组聚合，先按聚合值取最大 N，再按指定输出字段排序；Limit 参数绑定，留空受 10000 行保护。无分组为总量，SQL 模式不改写用户查询。
- `barDisplay.ts` 与 Dashboard 共用类别轴、格式与 tooltip；柱宽占分类带宽 80%，无固定像素上限；数值轴自动 K/M 缩写、tooltip 保留配置精度，分类标签水平显示、长文本截断并避让重叠，多分类保留滚动，单分类隐藏重复图例。支持负值/零值/空值、重名消歧与长标签。格式应用仅重绘，Top N 在 Run 后进入保存快照。`value` 为保留指标列；指标 value 分组请在 SQL 中另起别名。

## 饼图页签

- Pie/Search/Heatmap/Patterns 首次激活采用可取消微任务，兼容 StrictMode；来源草稿独立、切源清空结果并隔离迟到响应。`ExplorerResultStatus` 展示执行来源/绝对时间与未运行修改；隐藏页签卸载浮层。Pie/Heatmap 样本过滤恢复执行快照，`compileChartWhere` 统一绑定条件。
- `PieExplorer` 单序列 Builder/SQL，四来源均支持 Lucene/SQL 条件；`pieQuery.ts` 统一运行与转 SQL 校验。Metrics 复用 MetricPicker 的选名/属性过滤与 Delta，新建必须选名，旧未选名定义提示并保留口径；Traces 支持耗时表达式与 P50/P90/P95/P99，Sessions 仅计数/文本去重计数。`pieGroups.ts` 支持物理分类和参数化 JSON 路径，拒绝重复及保留列 value；含逗号 JSON key 使用安全列表解析。来源草稿、URL 与执行快照保留，保存仅引用已执行 SQL/参数并保留面板图例。
- 右侧显示抽屉复用数值格式字段（八类输出、小数位、千分位、大数缩写），应用才提交，序列格式优先。分类上限对返回结果按值取 Top N；查询仍受 10000 行保护，占比仅针对可见分类。
- `pieDisplay` 按完整元组去重并消歧重名；稳定配色、滚动图例、值与占比 tooltip。负值拒绝比例展示，零和空值不绘制虚假扇区。仪表盘复用同一渲染。

## 数值页签

- `MetricPicker` 在聚合与 Where 间提供可搜索指标选择、Tree/List 浏览和属性详情；目录/详情经 `api/chartMetrics.ts` 复用分析执行器及当前时间窗，详情最多 100 组标签。不伪造未存储的单位、描述、Resource 元数据。新 Metrics 序列必须选名，旧定义未选名保留原口径并提示；metricName/metricFilters 随原始序列保存，执行边界统一绑定。Metrics 不重复展示事件样本。
- `seriesAggregate.ts` 复用 Bar 的字段/JSON 路径和受限算术编译，支持 P50/P90/P95/P99、数值 Any 与白名单自定义聚合；拒绝任意函数/子查询。Delta 按指标与完整 labels 分区、窗口内相邻点求差，首点为空；未存储单调性信息，不推断重置或速率。Traces 明示 Span count、distinct trace_id 与微秒耗时；Sessions 沿用项目内去重口径，隐藏公式，不虚构时长/事件派生字段。

- Number/Time Series 每序列 `whereSyntax` 可选 SQL/Lucene，旧配置默认 SQL。复用 `WhereSuggestInput`：SQL 提示字段/关键字，Lucene 提示属性/值/JSON 路径；图表动态建议使用当前编辑时间窗，切源后无需先运行；JSON 属性采纳后提供值筛选和继续下钻入口。候选加载、空结果、缺少时间窗及失败重试显式呈现；facet 禁用/空 SQL 时不请求并清理旧候选。`chartWhere.ts` 统一编译 Run 与 Builder→SQL，Lucene、按值过滤及公式共享无冲突参数空间；快照保留原始条件，主值/样本/趋势共用编译结果。

- `NumberChart` 与仪表盘共用：整段时间聚合、大数居中；八类输出、单位、小数位、千分位、紧凑格式及稳定序列标识覆盖。设置抽屉草稿仅应用时提交；条件颜色按原值顺序判断，最后匹配优先，可上移/下移调整；共享类型位于 `api/chartTypes.ts`。
- 可选背景趋势使用 Builder 执行快照生成参数化时序查询，开启才取数；真实 UTC 时间轴保留缺桶，执行代次确保固定区间重跑刷新、旧响应丢弃。独立背景颜色不跟随阈值色；失败/截断提示不隐藏主值。SQL 模式不自动生成趋势。空值不转零，整数文本保留精度；p95 在原始窗口用 quantile_cont 计算，不聚合桶分位数。

## 公式

- `formula.ts` 将有限数字、输入引用与四则运算解析为受限 AST（≤512 字符、128 token、24 层，≤8 公式/12 输入）；不使用 eval/Function。数值和展示名参数绑定，SQL 结构仅由白名单节点生成。
- 输入引用 A/B 等独立于别名和数组位置，删除不重编号；公式只引用输入，不引用其它公式。聚合结果按相同时间桶与完整分组元组透视后计算；NULL、缺失输入、除零及非有限结果均为空，不补零。
- 执行快照包含公式；输入显示开关仅过滤展示行，不重查、不改变样本范围。数值/表格/图表与仪表盘共享结果适配，内部标识不显示为表格列。公式首版不支持自由 SQL、事件搜索和模式类视图。

## 决策

- 侧边导航前五项按总览、搜索、已保存搜索、图表探索、仪表盘排列，其余入口保持原有相对顺序；完整顺序见 [页面设计](../ui-pages.md)。

- 执行失败（校验不通过）返回 400 + 原因；运行期错误按执行器现有异常映射处理，不保证均为 400。
- 主搜索采用参数化 LIMIT/OFFSET 分批追加，SQL 每批探测 N+1 行、接口 rowLimit=N；limit 为每批大小，单次仍受 10000 行保护。续页固定已执行 SQL/参数/绝对时间窗，不重查直方图；表格内部 IntersectionObserver 触底加载，追加保留滚动与展开状态。新查询/刷新重置分页并丢弃旧响应；失败保留已有行、同 offset 重试。仅末页成功时显示 End of Results（中文「已显示全部结果」），无顶部截断或显示上限文案。用户排序后附内部字段确定平局顺序；跨请求迟到数据仍可能造成 OFFSET 位移，不承诺事务快照一致性。
- 搜索 SQL 直查不复用 DSL 编译器：表选择权交给用户 SQL（占位符只负责按天分表展开与时间参数绑定），这是统一搜索页替换自造查询语言后的唯一入口；`/api/{logs|traces}/search` DSL 端点保留作 API 兼容，UI 不再使用。
- 搜索页 Lucene 属性搜索在前端编译而非后端新增语言：编译产物仍是搜索 SQL 直查（唯一通道不扩张），值经 `Parameters` 字典以 DuckDBParameter 绑定——不把外部值拼进 SQL 文本（AGENTS.md 参数绑定强制项，且只读黑名单 `\bdelete\b` 词边界匹配会误杀 `LIKE '%delete%'` 字面量）；标识符（显示字段/排序）无法参数化，改以前端表结构白名单校验兜底。
- 保存查询不带参数占位符（后续可扩展 `{{param}}` 替换，替换后仍走同一执行器）。
- 保存搜索复用 saved_query 表（而非新实体）：同一 CRUD/API 面板，旧数据 Kind 默认 `sql`，SchemaReconciler 增量补列（只增不删）；definition Lucene 模式存 `mode/where/fields/orderBy/limit`，SQL 模式存 `mode/sql`，历史 `sql`/`query` 字段仅作打开回退。
- 已保存搜索列表工具条使用占满宽度的 `QueryBar`，名称筛选框与新建搜索按钮按顺序左对齐；不修改共用 `Page` 默认布局。
- 已保存搜索的「打开」复用同一 `SearchPage` 组件（`/saved-searches/{id}` 路由注入 `savedId` prop，零复制）：进入上下文时经 `GET /analytics/queries/{id}` 加载快照并应用（打开即自动查询），此时保存动作走 `Id≠0` 更新分支覆盖 Definition（保存即更新）；`/search` 页保存始终新建（`Id=0`）。前端搜索条件/筛选/时间窗全量快照在 `Definition` JSON，不新增后端状态。
- 搜索页 Event Patterns（事件模式聚类）仅界面占位：DuckDB 无内置日志模式聚类，客户端采样子集聚类语义有限，待真实需求落地再实现。
- URL 条件同步采用「条件状态 → 地址单向投影」而非 URL 为唯一事实源：`source` 保持 URL 派生（既有 `/logs /traces` 重定向与 `/search?source=` 约定依赖），其余条件 useState 承载、仅在挂载时一次性从 query 读入；`traceId` 为既有深链参数原样透传（深链落地后条件已物化为 filters，保留参数维持「浏览器回退返回日志视图」语义与分享链接形态）；视图偏好（表格/JSON、事件模式）与瀑布图弹层不入 URL。其它页面（/metrics、/analytics、/iot、/rum）后续按同一模式接入（TASKS T961）；图表探索采用下述独立协议。
- 跨数据域的条件适配（默认字段/排序跟随、过滤器清空、详情抽屉关闭）收敛为渲染期派生修正（React 官方「props 变化重置状态」模式），统一覆盖选择器切域 / traceId 深链 / 浏览器回退还原 URL 三条路径——浏览器回退只还原 URL 不还原组件状态，effect 链做适配会与首查 effect 同提交读到旧闭包，渲染期修正保证 effects 执行时状态已适配；已自定义的字段/排序保持不动（非法值经编译白名单校验报错）。

## 图表事件搜索

- `EventSearchExplorer.tsx` 独立承载 Search 页签：四来源均支持 Lucene/SQL WHERE，复用 WhereSuggestInput 与 LuceneSearchHelp；日志/链路默认 Lucene，指标/会话默认 SQL，切换不翻译或自动执行。日志/链路复用列选择、ResultsTable 和详情抽屉，指标/会话使用领域字段与详情。隐藏聚合控件，不请求重复样本；编辑状态按来源保留，结果绑定执行快照；会话条件作用于窗口内按项目和会话去重后的记录。
- `eventSearchQuery.ts` 校验显示列/排序白名单与 JSON 路径，Lucene、路径和按值过滤参数绑定，日志 id 以字符串返回。每批 200 行、N+1 探测、固定时间与 SQL 的 OFFSET 续页，旧响应丢弃，失败原位置重试；沿用已有 OFFSET 对迟到数据的限制。
- 保存至仪表盘仍为 table 面板，SQL 上限 10000，不保存临时 page_limit/page_offset。首次激活自动查询，之后 Run 提交编辑；不接管搜索页 URL 或自动刷新。

- 图表 Patterns：`PatternsExplorer` 独立编辑/执行快照，`patterns.ts` 经只读搜索执行器做完整时间窗规则归一化（动态数字、UUID、地址等→`<*>`），按模板/级别统计，30 桶趋势、最新代表事件、最多 200 个模板并显示总数。表达式限字段/绑定 JSON 路径及 lower/upper/trim；不是 HyperDX 内部聚类算法。下钻保留原始条件与结构化过滤，编辑、再次运行和保存一致；Sessions 按事件统计而非去重会话。仪表盘复用 PatternsTable 展示趋势与模板，搜索页模式占位不变。

## 数值分布热力图

- `HeatmapExplorer` 独立维护筛选、显示和执行快照；默认链路 duration_us，另支持指标 value。链路筛选复用 Lucene 参数编译，SQL 条件仍走只读分析通道。
- `heatmap.ts` 在 DuckDB 全量计数；新链路默认 60 桶与保留零值的有符号 log10(1+abs(x))，指标默认线性且保留负值。数值 10–80 桶，时间步长按实际窗口与总格数≤9600 自适应；旧缺尺度配置沿用线性与原粒度。空值、非有限值及负耗时不参与。色强独立支持 log1p(count)，提示保留真实计数；空格留白、轴标签紧凑。
- Explorer 点击格子独立查询样本（先筛选再限500），复用相同分桶公式避免浮点边界漂移；时间框选/Shift滚轮重查并可恢复上一范围。下钻只使用执行快照，选择不保存；分桶设置 Run 生效、色阶即时生效，URL/面板持久化尺度。
- 保存携带 `explorer.heatmap`，仪表盘复用渲染并按实际窗口绑定自动粒度；无此配置的旧类别热力图保留原语义。
