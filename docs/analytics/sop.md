# analytics — SOP

搜索告警的使用、资源预算和故障处置见 [Alerts 运维](../alerts/sop.md)；先删除关联规则再删除保存搜索。

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

- Go 回归：在 `go` 下运行 `go test -tags duckdb_use_lib ./cmd/sdata ./internal/store -run 'TestAnalytics|TestSavedQuery|TestUnmodifiedFrontend'`（Windows CGO 环境见 Go README）。使用临时库，不复用生产目录；`kind=search/analytics` 为快照，执行接口拒绝将其当 SQL。不因接口返回 200 就认定迁移完成，仍须检查结果值与截断。

- 工作台验收：更新并重启后端后进入 `/analytics`，刷新目录检查实际字段类型/可空性；选表后表列表与结构平分剩余高度，各自滚动且搜索框保持可见。分别筛选表名和字段名，确认互不影响，切表清空字段筛选；结构/示例不改草稿。查询栏保持紧凑双行，模式下拉固定宽度，Lucene 输入占中间可用空间，时间/行数/执行靠右；窄屏允许换行，不遮挡控件。SQL 补全区分物理表和逻辑来源，Ctrl/Cmd+Enter 执行；Lucene 检查五类来源、AND/OR/NOT、参数绑定、行数及保存恢复。目录返回 HTML 时检查后端版本与代理。亮暗/窄屏、键盘与 IME 需真实浏览器验收。
- 图表链接验收：八个页签切换、编辑来源/条件/时间、运行及表头排序后检查地址变化，复制到新页面应恢复相同条件并首查；相对时间按打开时刻重算，绝对时间不漂移。验证导航返回和仪表盘编辑不改写地址；非法链接不崩溃。URL 含查询值，分享前检查敏感信息，复杂条件长链接需确认代理长度限制。回归 `chartUrlState.test.ts`、`ChartExplorer.test.tsx`，真实浏览器链路另行验收。
- 时序 SQL 验收：Builder 切到 SQL 后说明默认展开，可折叠和复制示例；编辑区 260px，时间/运行在下方左对齐，数据源行展示告警和显示设置。SQL 模式无序列/公式/粒度操作；切回 Builder 保留配置。SQL 返回 `ts_bucket/value`，按 `series` 或其他维度拆线；时间过滤须显式写 `$start/$end`，切换来源时示例与编辑器同步，首次生成对应查询，返回恢复该来源草稿和绑定参数；旧结果清空且点击 Run 后才查询。检查亮暗和窄屏；专项回归 `ChartExplorer.test.tsx`。
- 表格验收：Table 独立编辑，配置共享分组、多聚合/公式、结构化多列排序与 Top N（默认10，1–500）；确认完整聚合后取排行榜、无时间桶。Builder 表头排序重新查询，SQL 表头只排返回行；CSV 导出返回结果。验证逐列格式、斑马纹、吸顶和四来源分组下钻。保存使用已执行配置，修改未运行草稿不得混入。回归运行 `TableExplorer.test.tsx`、`table.test.tsx`、`tableFixture.test.ts`、保存/面板测试及 Store 的 Table 夹具测试；登录后补亮暗、窄屏和保存重开。
- 公共样本验收：Time Series/Bar/Pie/Heatmap 使用相同折叠区、数量与样本范围提示；日志箭头展开、点击开详情、Trace 跳转，指标开 JSON 详情，会话按项目和会话身份回放。修改未运行草稿后按值过滤须恢复执行窗口；仅此值替换同字段条件，删除过滤自动重查。图表失败而样本成功时仍可查看样本，SQL 模式不重复查询样本。回归 `SampleMatchedEvents.test.tsx`、`bar.test.tsx`、`ChartExplorer.test.tsx`、`chartPanel.test.ts`。
- Bar 验收：单分类柱按分类带宽铺开，不应显示为固定细线；数值轴自动 K/M 缩写，tooltip 保留原精度，X 轴类别标签水平显示，长文本截断并避让重叠，不转为时间轴。Count 隐藏 Column；Sum/Avg/p95 等显示可编辑的 SQL Column，支持字段提示、JSON 路径和四则运算（如 duration_us / 1000），切回 Count 后返回保留草稿。WHERE 下拉切换 Lucene/SQL，各自保留草稿；Lucene 提示字段/JSON 子属性/实际值，SQL 提示字段与关键字，方向键选择、Enter/Tab 采纳、Esc 关闭。SQL 条件字面量绑定，拒绝注释/多语句。单序列、无粒度/公式/复制入口；完整窗口分类聚合。Group By 可多字段，排序仅选输出列；分类上限先取最大值再展示排序，修改后 Run 生效，格式应用不重查。SQL 返回数值 `value` 及类别列；负值跨零轴、空值不补零。回归 `bar.test.tsx`、保存/面板测试及 `ExecuteSearchAsync_Bar_CrossDayTopN`，保存重开核对格式、参数和旧布局。
- 端点：`POST /api/analytics/query {sql,rowLimit}`；`POST /api/analytics/search {sql,from,to,rowLimit,parameters?}`；保存查询 CRUD `/api/analytics/queries`（含 `GET /{id}` 查单条，仅需登录）。
- Number/Time Series WHERE：逐序列选择 SQL 或 Lucene，切换不翻译或自动运行。SQL 提示字段/关键字，字面量转参数；Lucene 提示字段/JSON 子属性/值，图表属性和值建议使用当前编辑时间窗，切源后无需先运行。输入 attributes[ 选择属性后，可点「筛选此属性的值」或「展开子属性」继续；无子属性显示空态，失败显示原因并可重试。方向键选择、Enter/Tab 采纳不运行，Esc 关闭，非法条件阻止请求。回归 `chartWhere.test.ts`、`ChartExplorer.test.tsx`、`WhereSuggestInput.test.tsx`。
- Number 来源验收：Metrics 必须逐序列选名，搜索下拉立即回填但不运行，浏览器 Tree/List 选择仅确认后提交；取消/Esc/遮罩不改草稿。展开指标查看真实标签并添加/移除结构化筛选，检查无数据/失败/截断提示。Delta 为窗口内相邻差值，非速率或重置推断。Traces 验证 duration_us / 1000、分位数及 distinct trace_id；Sessions 无公式，按项目去重，不支持不存在的时长/事件数。保存重开核对名称、Delta、属性条件；旧未选名图表保留原口径并提示。回归 `MetricPicker.test.tsx`、`numberSources.test.ts`、`numberFixture.test.ts`、`chartPanel.test.ts` 和 Store `ExecuteSearchAsync_Number_SourceSemantics`。
- 数值页签：切换 Number 聚合整段时间，隐藏分组/粒度；显示设置可调整格式、条件色和背景趋势，序列格式可继承或覆盖。应用才提交，关闭放弃草稿；SQL 需返回 `value` 列且不自动生成趋势。阈值比较输入单位的原值，最后匹配优先，上移/下移后应用生效。趋势可单独设色，按真实时间保留缺桶，固定区间重跑也刷新；失败不影响主值，截断显式提示。吞吐格式不自动计算速率。专项回归：`number.test.tsx`。
- 菜单验收：前五项依次为总览、搜索、已保存搜索、图表探索、仪表盘；其余入口保持原有相对顺序。简体中文应显示「仪表盘」「客户端会话」，切到英语应即时显示「Dashboards」「Client Sessions」。排序与双语回归执行 `npm --prefix ui run test -- src/layouts/components/__tests__/AppSider.test.tsx`。
- 已保存搜索布局验收：名称筛选框与「新建搜索」按钮在列表上方靠左依次排列；回归执行 `npm --prefix ui run test -- src/pages/saved-searches/__tests__/SavedSearchesPage.test.tsx`。
- 已保存搜索语义：`/search` 页「保存当前搜索」= 新建（Id=0）；`/saved-searches/{id}` 打开后 = 更新当前条目（Id≠0 覆盖 Definition，保存即更新不新建）；写操作均限 AdminOrWrite。
- 仅允许单条 SELECT/WITH；写关键字（insert/update/delete/create/drop/…）任何位置出现都会被拒（含注释与字符串字面量）。
- 搜索端点外部值一律参数绑定：`$start/$end` 绑定请求时间范围，其余 `$name` 引用绑定 `parameters` 字典（键小写字母开头、≤64 个、值字符串/数字/布尔/null；被引用但缺失返回 400）——不要把用户值拼进 SQL 文本。
- 表名按天（`logs_20261115`）；先用 `SELECT table_name FROM duckdb_tables()` 探查。
- 行数上限硬保护10000，在数据库执行层 LIMIT N+1 并流式读取；大结果导出按时间分批。DuckDb.QueryTimeoutSeconds 默认30秒（1–300），覆盖执行器等待与原生计算，超时408应缩小范围/简化表达式。LIMIT不限制排序、聚合全部中间内存，单个巨大值也可能超过配额；不要以加内存代替执行边界。
- 饼图来源验收：Metrics 先选指标，展开属性添加/移除过滤，按 `labels["host"]` 等 JSON 路径分类；Delta 不跨标签且负值拒绝比例展示。Traces 验证 Count=Span、distinct trace_id=Trace、P99 与 duration_us / 1000；Sessions 验证按项目去重后按 domain 分类，数值聚合不可选。切源再返回恢复配置，未选指标及 value/重复/非法分类阻止查询。运行 `pie.test.tsx`、`pieQuery.test.ts`、`pieFixture.test.ts` 与 Store `ExecuteSearchAsync_Pie_SourceSemantics`；真实浏览器需登录后核对四来源与保存重开。
- 饼图验收：进入 Pie 后默认单序列计数，无分组亦可运行；设置分组后查看分类图例和值/占比提示。显示设置右侧抽屉点击应用才提交，关闭取消；分类上限保留最大值，占比针对显示分类。SQL 模式需返回 value 数值列，负值和全零结果不绘制比例。回归 `npm --prefix ui test -- src/pages/charts/__tests__/pie.test.tsx`。
- 搜索筛选同步验收：勾选两个服务名应在 Lucene 输入框显示 OR 分组；取消、排除、仅此值、删除 chip 同步更新，手写 OR 分组保留且不自动运行。手改自动子句后旧 chip 清除，不得隐藏生效；清空输入应清除所有自动限制。验证降噪、存在性、耗时、根 Span、SQL 切回 Lucene、旧分享链接/保存重开与 traceId 深链；运行后行查询及直方图条件一致且无重复绑定。专项回归 `filterExpression.test.ts` 与 `SearchPage.test.tsx`；真实浏览器交互另行验收。
- Lucene 使用说明验收：输入框下默认展开，编辑条件与已执行结果明确区分；空表达式仍提示时间/过滤/降噪限制。展开收起不改条件、不运行查询；日志/链路匹配范围随来源更新，SQL 模式隐藏。核对双语示例和侧栏存在性指引、亮暗与窄屏换行，回归 `LuceneSearchHelp.test.tsx`、`SearchPage.test.tsx`、`WhereSuggestInput.test.tsx`、`lucene.test.ts`。
- 图表 Lucene 说明验收：时序/数值、柱状、饼图、事件搜索、热力图、模式各页签切换 Lucene 后默认展示，SQL 隐藏；多序列各自草稿和折叠独立，四来源示例字段适用，不出现搜索页降噪或左侧过滤器指引。说明操作不运行查询，仪表盘编辑沿用；专项回归各 Explorer 与 `LuceneSearchHelp.test.tsx`，登录后补亮暗、窄屏和面板弹窗视觉验收。
- 搜索列表触底自动加载下一批，工具栏行数为每批大小，不是累计显示上限；CSV 仅导出已加载行。续页失败保留已有数据，底部重试继续同一页。
- 验收：不出现顶部截断或显示上限提示；连续触底应追加且不跳动，仅无更多数据时显示 End of Results（中文「已显示全部结果」）。新查询/定时刷新重置到第一页；深度浏览可关闭自动刷新。自动刷新时展开属性节点、输入值搜索并滚动侧栏，确认不重载筛选树、不丢焦点、不闪空或重播直方图动画；候选计数锚定最近显式查询，手动 Run/换范围/切源才更新。慢请求不得叠加轮询，后台失败保留旧结果并提示，手动 Run 仍可立即查询。加载中/失败不显示结束提示，零结果保留空态。
- SQL 预览验收：数据源旁与 Run 旁操作菜单均选择「预览 SQL」，应打开只读高亮模态窗且不自动复制；SQL 直接展示正确转义的实际条件值，仅保留 $start/$end；复制内容与预览一致，执行和保存仍使用绑定参数。点击窗内复制后才写剪贴板，Esc/遮罩可关闭。
- 公式验收：配置输入 A/B→添加公式→输入 `A / (A + B) * 100`→运行；A/B 相同且非零时应为 50，关闭显示输入序列后只保留公式。空表达式/未知引用应阻止查询，除零/缺失值显示为空。公式限 8 条、输入限 12 条、表达式限 512 字符；复杂度或参数数量超限时缩减配置。
- 四来源验收：依次选择 Logs/Metrics/Sessions/Traces 运行；Sessions 为客户端 RUM 会话而非服务端链路，按项目与会话去重。Run 菜单可保存到仪表盘；只读用户无保存入口。图表缩放同步重查，手选粒度不随时间跨度强制覆盖。
- 图表探索页（/charts）验收：相对预设每次运行锚定 now、绝对区间重复运行不漂移；时序用真实时间坐标（缺失桶断线不补零、不隐式堆叠）；样本区行首箭头就地展开、行点击开详情抽屉，按值过滤生成 $pN 参数条件 chip 并自动重查；「生成的 SQL」为只读 CodeMirror 高亮并直接展示实际条件值，复制与展示一致，样本查询失败显式报错且不影响图表。回归执行 `npm --prefix ui run test -- src/pages/charts/__tests__/ChartExplorer.test.tsx`。

- 事件搜索验收：进入 Search 页签应只见名称、来源、SELECT、条件、排序、时间与运行；默认日志列 ts/service/severity/body，无聚合/公式/粒度/样本区。日志箭头展开、点击行开详情，列头排序与按值过滤使用执行快照；触底追加且失败可重试。四来源均可选 Lucene/SQL，切换不翻译或自动运行；指标/会话沿用默认 SQL，可切 Lucene 使用各自字段条件、裸词及来源说明，非法字段不发请求。会话条件作用于窗口内去重后的记录；更多结果用底部按钮加载，续页固定执行条件，保存重开恢复原始语言与条件。
- 事件搜索回归：`npm --prefix ui run test -- src/pages/charts/__tests__/EventSearchExplorer.test.tsx src/pages/charts/__tests__/eventSearchQuery.test.ts`。浏览器完整验收还需覆盖链路、亮色主题、列选择、失败与连续触底；自动测试不能代替这些人工验收。

- 模式验收：进入 Patterns，仅显示名称/来源/模式表达式/条件/时间与运行；默认 body（链路和指标 name、会话 domain）。结果四列为趋势/数量/级别/模板，点击模板打开代表事件，支持换行、复制、CSV 与 SQL 预览。计数是完整匹配窗口的规则分组，非抽样估计；超过 200 个模式时缩小范围查看。表达式仅支持字段、JSON 路径、lower/upper/trim，不支持任意 SQL 表达式。
- 模式回归：`npm --prefix ui run test -- src/pages/charts/__tests__/patterns.test.ts src/pages/charts/__tests__/PatternsExplorer.test.tsx`；`dotnet test tests/SData.Store.Tests --filter FullyQualifiedName~ExecuteSearchAsync_Patterns_CountsAndTrendsAgree`。SQL 夹具 `Fixtures/patterns.json` 来自同一前端构建器，修改构建器后须同步重生成。

- 热力图验收：进入 Heatmap 默认 Traces + Lucene，隐藏序列/公式/分组；运行后横轴时间、纵轴耗时区间、色阶 Low→High，悬停显示时间段/数值区间/计数。新链路默认对数60桶；显示设置支持10/20/40/60/80桶、线性/对数数值尺度与独立计数色阶，改分桶后 Run 才提交，色阶即时应用。点击格子应只展示该格样本，清除返回全局样本；框选工具与Shift滚轮放大后可恢复上一范围。修改未运行草稿再下钻不得混入草稿。保存后在仪表盘变更时间范围检查自适应粒度，旧配置保持线性；登录后补亮暗、窄面板及保存重开验收。
- 热力图回归：`npm --prefix ui run test -- src/pages/charts/__tests__/heatmap.test.ts src/pages/charts/__tests__/HeatmapExplorer.test.tsx`；真实 SQL 测试 `ExecuteSearchAsync_Heatmap_CountsFullDistribution`。夹具 `tests/SData.Store.Tests/Fixtures/heatmap.json` 来自前端同一构建器，修改 SQL 后同步生成。

- Table 默认体验：打开后应自动查询最近24小时服务排行；切换指标应变为 name/avg(value)，不能残留旧服务计数。点击常用预设可直接运行；普通筛选用字段/操作符/真实值建议，高级 SQL 条件仍需遵守只读查询规则。无结果先检查来源与时间，再显式扩大至7天，不填充演示数据。
