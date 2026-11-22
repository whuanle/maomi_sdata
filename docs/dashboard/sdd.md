# dashboard — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[analytics](../analytics/sdd.md)、[store](../store/sdd.md)、[metrics](../metrics/sdd.md)

## 目标

普通仪表盘配置化：面板按 `query.kind` 分派到既有执行器（原生指标 / 统一 DSL 聚合 / PromQL 子集 / 分析 SQL），一次请求拉取全部面板数据，前端按面板类型渲染（line/area/bar/stat/table），并提供可视化面板编辑器（查询构建器 + 实时预览）。

## 组件

- Go 适配：元数据、配置验证、面板分派与有界算术图分别位于 `go/internal/query/dashboards.go`、`panel_validation.go`、`panels.go`、`custom_panels.go`。保留原始 `configJson` 字符串（包括未知扩展字段），仅服务 view；更新用单条条件 UPDATE 防止旧草稿覆盖，HTTP 层不包含业务 SQL。
- Go SQL 面板复用 Store 分析通道；metric/DSL/PromQL 复用对应查询服务。静态面板不执行查询，组容器仅执行叶子，全部返回原 `PanelData` 形状；节点仅接受有界算术 AST，不执行宿主代码。

- `DashboardEntity`（SQLite）：name、kind（活动类型仅 view；历史 screen 原样保留）、config_json。
- **面板协议（version=1，`DashboardConfig`）**：`{version,time:{preset},refreshSeconds,panels:[{id,title,type,layout:{w,h},unit,showLegend,query:{kind,...}}]}`。旧协议（面板根挂 sql/queryId/rowLimit、无 query 字段）兼容解析与执行。
- `PanelQueryConfig`（Dashboard.Shared）：kind 多态平铺——
  - `metric`：name/labels/aggregation/stepSeconds/maxSeries → `IMetricStore.QueryRangeAsync`；
  - `dsl`：domain(logs/traces/metrics/iot) + `QueryDslModel`（bucket/groupBy/aggregations/filters；range 由仪表盘时间覆盖）→ `IQueryDslExecutor`；
  - `promql`：expr/stepSeconds → `IPromqlExecutor`；
  - `sql`：sql/queryId → `IAnalyticsExecutor`（含 `{{logs}}` 等表占位符时走 `ExecuteSearchAsync`，区间感知 + 参数绑定）。
- `PanelQueryExecutor`（Dashboard.Core）：按 kind 分派；序列形态统一为 `PanelSeries[]`（与 MetricStore.SeriesResponse 对齐），表格形态直传 columns/rows；单面板异常转 `PanelData.Error` 不中断其它面板；序列数量受 maxSeries 上限（1–100）。
- `PreviewPanelCommand` + `POST /api/dashboards/preview-panel`：未保存面板的实时预览（编辑器防抖调用）。
- `PanelQueryValidator`：Shared 层查询形态校验（PreviewPanelCommand 校验器复用；保存侧结构校验待二期收紧）。
- `GET /api/dashboards/{id}/data?from&to`：支持时间范围覆盖（缺省 = 仪表盘 time.preset，缺省 1h；解析见 `PanelRange`）。

## 前端

- `PanelCard`（渲染器）：series → line/area/bar（echarts）/stat（序列末值大数）；columns+rows → 表格；单位格式化 short/ms/bytes/percent/ops；查看态与编辑器预览共用。
- `PanelEditorDrawer`（编辑器）：基本信息（标题/类型/栅格 w,h/单位/图例）+ 按 kind 的查询构建器（metric：指标名自动补全 + 标签过滤行 + 聚合/步长；dsl：域 + 时间序列/聚合表格双模式 + 过滤行/分组/聚合行；promql：表达式 + 步长；sql：SQL 文本）+ 防抖实时预览；切换 kind 时初始化默认查询体保证预览即刻可用。
- `DashboardsPage`：列表、搜索、新建与整盘导入导出；`DashboardDetailPage`：时间范围、自动刷新、自由画布和整盘草稿保存；`ChartPanelEditor` 复用 Explorer，`PanelEditorDrawer` 保留高级查询与静态面板入口。
- 详情头部（2026-11-19）：AppLayout 精确匹配 `/dashboards/:id` 后不加 padding，由详情统一提供顶部 8px、水平 12px 间距；名称、操作与查询控件合并为同一 QueryBar，操作紧跟定宽名称输入框靠左排列（无自动右推），时间范围、自动刷新及运行接在展示按钮之后，查看态“编辑面板”紧接运行按钮，未保存提示仍在操作后；详情不展示描述输入框，保存时保留已有描述数据。长名称截断、控件换行，保留唯一外层滚动。展示路由独立使用紧凑间距，列表及其他页面不变。

## 决策

- 面板数据端点合并返回，减少仪表盘轮询请求数；面板级失败不影响其它面板。
- 查询语义不重复造轮子：面板协议只是既有四个执行器的编排层，不做新的 SQL 组装（契约 C1/C2 全程参数绑定）。
- PromQL 区间语义修正（2026-11-18）：rate 类表达式每评估点按声明窗口向前回看（Prometheus 语义，窗口可跨 step 桶），旧实现把数据窗口钳制在单桶内导致 rate 在 step < range 时恒为 0——见 `PromqlExecutor.RateLookback`。
- Grafana JSON 兼容不做底层承诺：后续只在协议层加「外部 JSON → PanelConfig」导入转换器（本期未做）；SData 原生格式的整盘/面板导入导出已于 2026-11-20 提供，见下节。

## 拖拽画布工作流（2026-11-18）

- 导航合并为唯一 Dashboards 入口（`/metrics` 重定向；指标 API 保留供组件查询）。
- 布局协议 version=2：`layout` 增加 `x/y`（12 列网格，行高 96px + 12px 间距）；旧 v1/无版本配置按原顺序由 `normalizeDashboard` 迁移为不重叠坐标，`screen` 等扩展字段保留，仅在用户保存时写入新版本。
- 独立路由：`/dashboards/:id` 草稿画布（增删改/复制/上下移/拖动/resize 仅改本地草稿，整盘保存才落库；离开未保存有拦截）、`/dashboards/:id/present` 展示模式（AppLayout 外，无侧栏，普通 Dashboard 可展示）。
- 布局引擎：react-grid-layout 2.3（维护中、peer React>=16.3），封装为 `design-system/DashboardGrid`（标题手柄拖动、se 缩放、基于 noCompactor 的临时重叠策略保持自由坐标）；`ui/AGENTS.md` 增补窄范围例外——仅布局适配层可生成运行时定位样式。
- 保存校验：`DashboardConfigValidator` 校验版本、面板 ID 唯一、类型白名单、坐标/尺寸范围与查询形态；`PanelRange` custom preset 现在返回显式 from/to。
- 重叠排序（2026-11-19）：拖动期间基于起始快照实时计算前插/后插，拖动项跟手、邻居立即预览让位且占位框指示最终落点，来回移动不累计碰撞推动；松手才提交草稿，仅重排源与目标之间的视觉区间，保留各组件尺寸及区间外主动留白；上下移共用纯函数，缩放单次避碰。归一化仅用于加载迁移，不在草稿更新时二次向下推动。相同网格落点复用预览结果，位置查找使用Map；仅邻居transform保留100ms过渡，拖动项、占位框与容器高度取消过渡，减少跟手延迟并尊重减少动态效果偏好。
- Group（2026-11-19）：含容器使用布局 v3，`type=group` 的 `panels` 为有序叶子、`defaultPanelId` 兼容历史默认标签。新建仅填名称，允许空成员列表；编辑时添加、移出和排序，移出最后一个成员仍保留容器。组件拖入Group中央高亮收纳，边缘维持排序，Group不能拖入Group。禁止嵌套和全局重复 ID，容器与叶子合计最多100项；不改数据库。Group整体拖动缩放，成员可编辑/复制/删除、移出和排序，解散保留成员；全部叶子沿用原数据端点预取，容器不执行查询。Tab切换只改本地查看状态，布局和顺序变化不重查。
- 草稿取数：已保存面板走 `/data`，草稿查询走 `preview-panel`（画布时间范围传入；并发 ≤3、过期响应丢弃）。

## Explorer 保存（2026-11-18）

默认 avg(num_value)，来源包含 HTTP 遥测；不扩展热力图。
- Search/Patterns 仍保存为 table 协议，按 explorer.chartType 分派展示：Search 复用 ChartTable 的分页与内部列过滤，Patterns 复用 PatternsTable 的趋势、计数和模板；面板不发起探索器续页或代表事件补查。
- Bar 保存已执行 SQL/参数及 `explorer.barSettings/barGroupBy/orderBy`，强制关闭自动粒度；`barDisplay.ts` 同供 Explorer 与面板渲染，序列格式优先，负值与空值保持原义。通过 `explorer.chartType` 区分类别 Bar 和 Time Series 的柱形模式，不改旧时序面板。
- Table 保存已执行宽表 SQL、独立绑定参数及 `explorer.tableQuery/tableColumns/tableSettings/tableGroupBy`；Top N 同步 rowLimit，保存 SQL 不包含 N+1 探测行。Dashboard 继续重绑时间范围，共用 `ChartTable` 格式、斑马纹、吸顶及导出；旧表格保持兼容。完整保存重开浏览器验收被登录阻断。
- Pie 保存为 pie，`explorer.pieSettings` 保留分类上限与全局数值格式，`series[].format` 保留序列覆盖；PanelCard 使用共享饼图渲染，缺配置的旧面板使用默认值。四来源 editor 保留指标名、属性过滤、Delta 和 JSON 分类，执行 SQL/参数继续按仪表盘时间重绑；编辑应用只引用已执行快照并保留原图例与布局。分类上限只影响展示，不改变只读查询通道。
- Number 保存为 stat，`explorer.numberSettings/numberFormats/numberTrend` 保留格式、条件色、稳定标识覆盖与趋势查询；`NumberChart` 同时供探索器和仪表盘使用，旧 v2 缺配置时使用默认值。背景趋势单独经分析接口按面板实际 from/to 取数，默认关闭且失败不影响主值，无新增后端执行通道。规则顺序最后匹配优先，独立背景色与原值阈值跨页面一致；每次面板数据更新触发趋势刷新，旧 stat 末值语义保持不变。序列 `whereSyntax` 与原始条件一并保存，实际执行仅使用已编译 SQL 和绑定参数，不在面板内重新解释 Lucene。

- `SaveChartModal` 保存已运行的查询，复用 normalizeDashboard/appendPanel，保留已有布局与扩展字段；只读隐藏入口，失败不关闭弹窗。
- query 增加 parameters/autoGranularity，过滤值经 ExecuteSearchAsync 绑定，chart_bucket 按仪表盘实际查询范围绑定；PanelData 返回 from/to 供时间轴使用。explorer 保留来源、序列与展示配置；pie/heatmap 加入类型白名单，与 Explorer 共用长表渲染。
- 分布热力图保存 `explorer.heatmap.scale/colorScale/bins` 并复用 analytics 渲染；新尺度 SQL 由实际 $start/$end 自适应，旧缺尺度保持线性，旧类别图不变。格子下钻与时间框选在 Explorer 进行，面板仅共享视觉与提示。
- 保存携带可选 expectedConfigJson；服务端事务内比对原配置，冲突返回 409。画布整盘保存亦携带基线，防止追加图表被旧草稿覆盖。

- Explorer 公式定义与输入显示开关存于 explorer v2，执行仍为参数化只读 SQL，无独立后端表达式解释器。数值面板展示所有可见公式，内部列隐藏；修改 SQL/查询形态或图表类型时清除不兼容的 Explorer 元信息。SQL 返回 truncated 由面板提示，保存的 Explorer 面板行数上限为 10000。

## Markdown 静态面板（2026-11-18）

- Explorer 按 HyperDX 的名称、200px 正文输入框、Preview、操作菜单布局；实时本地预览，无 Builder、时间范围或 Run。
- 面板使用 `type: markdown` 和根级 `markdown` 字段，正文最多 50000 字符，允许空内容；保存沿用布局追加与乐观并发，无需数据库迁移。
- Explorer、PanelCard 和编辑抽屉共用 `design-system/Markdown`（react-markdown + remark-gfm），不执行原始 HTML，过滤危险 URL，外链隔离 opener；不提供服务端抓取代理。
- 后端静态面板在查询分派前短路，编辑器不调用远程预览；旧 SQL/queryId 即使残留也不执行。

## Screens 下线（2026-11-18）

- 删除独立页面、菜单、路由及专属文案，不并入仪表盘或 RUM；普通仪表盘展示模式与刷新保留。
- 保存仅允许 view；列表在计数和分页前过滤 view；详情、数据、更新和删除只查 view，历史 screen 按 ID 返回 404，不能通过更新转换类型。
- 不改数据库结构，不删除或转换历史记录；配置中的未知扩展字段继续宽容解析。

## 查询、展示与编辑工作区（2026-11-19）

- 新建图表仅提供共享工作区，移除“高级查询 / Markdown”切换空 Metric 侧栏的入口；底部常驻取消/应用按钮，运行成功后由当前页签注册已执行 SQL/参数和展示配置的应用动作；加载、失败及切换无结果页签时注销，不再二次确认。旧侧栏仅兼容已有非 SQL 或 Markdown 面板。
- `/charts` 的 `ChartExplorerPage` 仅管理 URL；无 Router 依赖的 `ChartWorkspace` 供独立页和 `ChartPanelEditor` 共同使用。panel 模式继承画布时间、显式运行且不上报 URL，“应用”只改草稿。
- 面板 `query` 存执行 SQL/参数；`visualization.version=1` 存 kind、输出 ID/标签、格式、输入显隐/隐藏列及分布定义；根级 `editor.version=1` 恢复原始条件、公式与 Builder/SQL。Number 趋势移至 `query.numberTrend`，仍经分析只读通道。
- `panelDefinition.ts` 集中兼容：支持的新字段优先，缺失时读取旧 explorer；PanelCard 不读取编辑定义。新面板由同一 `chartToPanel` 生成兼容镜像；加载不写库、布局仍 v2，不做批量迁移。未知版本展示提示并禁止编辑覆盖。
- 保存和预览共用定义版本/枚举/大小校验。主查询身份包含 rowLimit、排除展示/编辑/背景趋势，拖拽及格式变化不重跑主查询；修改高级 SQL 清除过期编辑定义，兼容展示保留，切查询形态或类型重置不兼容定义。Explorer 工作区不提供全局变量、过滤或重复面板；跨查询表达式由下述独立自定义组件设计页提供。

## 共享图表编辑工作区（2026-11-18，历史兼容）

- `ChartPanelEditor` 使用宽屏工作区，独立 `/charts` 和画布通过 `ChartWorkspaceContext` 复用同一 Explorer；嵌入模式继承画布时间、显式运行预览，“应用”仅更新本地草稿，整盘保存才落库。
- `explorer.version=3` 增加 `editor.version=1`，保存原始 Builder/SQL 模式、序列、结构化筛选、分组、粒度和公式；编译 SQL/参数仍在 query 中。`chartToPanel/panelEditorDefinition` 统一保存与恢复，更新保留面板 ID、坐标及扩展字段。
- 无完整定义的旧 SQL 面板明确降级，保留原类型与 Explorer 展示配置，不反解析 SQL；metric/DSL/PromQL 和 Markdown 保留高级编辑入口。新定义在服务端校验版本、枚举、数组数量和长度边界。
- 沿用 12 列自由画布与现有公式解析器；窄屏仅重排显示并禁用几何写回，合法 v2 的 1×1 历史面板不再被加载时扩展。完整 Grafana 插件/PromQL、全局变量和折叠分组不在本轮范围。

## 自定义组件设计（2026-11-19）

- `PanelRoutePage` 统一 `panels/new?kind=custom|explorer|group`、`panels/:panelId/edit` 和 `panels/:panelId/preview`，按类型复用设计器/Explorer/兼容侧栏/分组编辑器，预览登录可用、写路由限制角色。默认新建仍为custom。
- URL的edit控制整盘模式，tab控制图表页签，tab.<groupId>控制分组成员，node/settings控制设计器位置；不存SQL/参数。DashboardSession仅内存保留草稿，Apply不落库，刷新恢复位置及已保存配置，不恢复未保存内容。编辑器离开确认，整盘保存沿用expectedConfigJson。
- Checkbox消费兼容children而非透传input，GroupEditor显式使用label，避免void-element渲染崩溃。
- `query.kind=custom`、custom.version=1 保存前序依赖图（最多12条SQL、8个表达式）。SQL独立参数绑定并经 IAnalyticsExecutor；表达式为受限算术AST与归约，不执行JS/HTML。完整时间/分组键对齐，标量广播、缺失/除零为null、重复键拒绝。
- 结果映射支持表格、标量、时间序列与分类数值；展示复用PanelCardBody，节点诊断提供原始结果、耗时和截断提示。单面板中间结果预算200000单元、单查询最多200列，输出最多10000行。
- 默认可视化查询按来源选择聚合、Lucene筛选、时间桶/分类/原始表；复用compileChartWhere/buildChartSql与WhereSuggestInput，值和指标属性参数绑定。节点editor v1只恢复配置，执行不解释它；autoGranularity按实际时间窗重绑。无editor的旧节点保持高级SQL，重建Builder需确认；引导式表达式提供输入选择、比率/差值/百分比/归约。
- 字段提示跟随节点来源与时间窗，两个定宽下拉和加载按钮桌面同一行、窄屏换行；节点可折叠，输出选择控制多数据叠加，显示名称不影响计算身份。
- SQL编辑器提供来源/字段、函数与绑定参数补全；数值格式/阈值、类别图格式、表格配置复用已有控件。未知版本禁止覆盖；不包含独立组件库、脚本插件或完整Grafana表达式。

## 导入导出（2026-11-20）

- 纯前端实现、无新端点：导入复用 `saveDashboard`（保存侧 `DashboardConfigValidator` 深度校验，非法结构 400）；导出为浏览器 Blob 下载。包装格式仅接受 `kind` 校验：整盘 `{kind:"sdata-dashboard",version:1,name,description,dashboard}`、面板 `{kind:"sdata-panel",version:1,title,panel}`；解析经 `normalizeDashboard` 做版本迁移与结构校验，面板导入递归重生成 id（分组 `defaultPanelId` 同步重映射），标题保留。
- 入口与权限：列表「导入」写权限（文件+粘贴双通道，名称/描述预填可改）；列表卡片与详情「导出」只读可用（详情导出当前草稿所见即所得）；编辑态「导入面板」经 `appendPanel` 追加画布底部，整盘保存才落库；画布组件操作含「导出」（分组含全部子面板）。

## 已知限制

- promql 序列名在聚合后为 `expr`（无 __name__），图例靠标签区分。
- dsl 时间序列形态仅支持单一聚合列（多聚合请用表格类型）。
- 列表页搜索为前端内存过滤；Screens 已下线，不再提供独立轮播。

## 共享图表组件（2026-11-18）

- `design-system/TimeSeriesChart` 供面板、预览与指标页复用；轴和提示框共享 `formatChartValue`，完整标签确保独立图例名称，缺失点保留 null，不按序列数隐式堆叠。
- `Chart` 经 `withChartTheme` 合并亮暗主题（显式 option 优先，支持数组组件），不向饼图强加坐标轴；重建 theme 后重绑事件并应用 option，时间序列允许零最小高度。

- Table 引导筛选与阈值编译后保存参数字典；下钻和面板 SQL 使用同一执行条件，不直接保存未运行草稿。
