# 页面功能模块与权限设计

## 关联

- [architecture.md](architecture.md) — 功能架构总览（菜单中的 RUM/仪表盘/接入页对应其 M3/M6 里程碑）
- `ui/AGENTS.md` — 前端硬规则（权限三值、只读隐藏写按钮、审计列、i18n）

权限与菜单对标 ClickStack（HyperDX）与 OpenObserve 的信息架构：观测页学 ClickStack 的 Search/Home，接入页学两者的 Ingestion 页（复制即用配置），RUM 学 OpenObserve RUM。不自造交互模式。

## 前端实现基线（2026-11-17 去 antd 化改造）

技术栈：React 19 + TypeScript + Vite 7 + **Tailwind CSS 4 + Radix 原语（自研 design-system v2）** + zustand + react-router 7 + echarts 5 + react-hook-form；antd 及 @ant-design/\* 已移除，组件代码全部归仓库所有（`ui/src/design-system/`）。视觉基准：**HyperDX 式暗色高密度观测台，默认暗色 + 可切亮色**（CSS 变量双主题，`data-theme` 桥同步切换，旧 8 套 antd 预设归一为亮/暗两族）。素净原则（2026-11-17 对齐 HyperDX ClickStack 调整）：纯中性灰底面（不带蓝调）、无渐变无光晕、小圆角（4/5/8px）、低饱和紫仅作主按钮/链接/选中态强调（暗 `#7A6FE0` / 亮 `#6459CE`）；字体只声明 `Inter` 未装即落 Helvetica/Arial（不走 Segoe UI 圆润回退链），数据列等宽字体（JetBrains Mono 栈）；图表色板取 HyperDX 分类 10 色，语义色取 Tailwind 500 档（success `#22C55E` / warning `#F59E0B` / error `#EF4444` / info `#437EEF`）。硬规则见 `ui/AGENTS.md`。

## 权限模型（定案：只读写分离，无角色/模块/接口权限矩阵）

复用现有 `SDataRoles` 三值 + 种子账户，对外表述为「账户级别 × 写权限」：

| 账户 | 来源 | 能力 |
|---|---|---|
| 超级管理员（super） | 系统初始化种子，唯一 | 全部权限；不可被删除/降级 |
| 管理员（admin） | 仅超管可创建 | 系统设置（含成员管理、接入 Key）+ 全部写权限 |
| 成员·读写（write） | 管理员创建 | 创建/修改平台资产（仪表盘、保存查询、设备台账、RUM 项目、面板）；只读用户不可见这些写按钮 |
| 成员·只读（readonly） | 管理员创建 | 仅查询与浏览；写按钮隐藏（前端 gating 只是体验层，后端授权是权威） |

- 目标保护规则（放 Handler，不放角色系统）：不可删除自己、不可删 super。
- 数据摄取与账户权限解耦：OTLP/ES/自有 JSON 走 IngestKey（仅管理员管理），MQTT 走已注册启用设备及管理员配置的免key/统一key/前缀认证，RUM 走公开 ProjectKey（成员·读写可建项目）。
- 「写」的边界：不包含用户/Key/系统设置（管理权专属 admin 及以上）。

## 菜单结构

侧边栏沿用 `AppSider` 的 mainNav 单组模式（原「管理」组已撤销）；成员管理并入「系统设置 → 成员」tab，页面内管理操作按角色显隐（后端授权是权威）。

菜单顺序（2026-11-19）：Overview → Search → Saved Searches → Chart Explorer → Service Map → Dashboards；其余菜单保持原有相对顺序。

### 观测

| 页面 | 路由 | 状态 | 对标 | 核心功能 |
|---|---|---|---|---|
| 总览 | `/overview` | 系统运行总览（2026-11-18） | ClickStack Home | 服务存活/运行时长/内存与系统信息、七域 UTC 今日数据量/天表/进程累计落库与丢弃、共享缓冲、系统/应用 CPU 与内存、线程池、磁盘容量/可用空间、DuckDB 追踪内存与文件/WAL、S3 对象大小及列举状态、MQ 实例运行摘要；10 秒刷新与失败旧快照提示，无最近日志或样本查询 |
| 统一搜索 | `/search` | 重构（2026-11-17，HyperDX 复刻版） | HyperDX Search | 单菜单搜索日志+链路，全页等宽（font-mono 13px）flat + hairline 分区（无卡片容器，布局区域一律细线分隔，仅表单控件带边框）HyperDX 式布局：顶栏行 1（数据源切换 + SELECT 关键字条→列选择浮层：物理列复选 + JSON 属性发现树（attributes/resource 逐层懒加载展开子属性、勾选即添加括号路径结果列，日志域 body、链路域 events 同样可展开；发现锚定当前搜索时间窗抽样口径，支持按键名搜索/Load more/失败重试；自定义路径手填为补充入口）、ts 常驻；ORDER BY 关键字条→行式排序编辑浮层：字段/ASC/DESC/增删行；变更即时重查；单一「保存搜索」入口（ClickStack 式弹层：保存当前搜索 + 已保存列表，点击应用/删除，不再重复双按钮）；SELECT/ORDER BY 关键字条 flex 弹性拉伸（约 3:1）填满行 1 至右侧保存操作，触发器 w-full 撑满容器不按内容收缩）、行 1（两种语法同构不变）：数据源切换 + SELECT 关键字条→列选择浮层：物理列复选 + JSON 属性发现树（attributes/resource 逐层懒加载展开子属性、勾选即添加括号路径结果列，日志域 body、链路域 events 同样可展开；发现锚定当前搜索时间窗抽样口径，支持按键名搜索/Load more/失败重试；自定义路径手填为补充入口）、ts 常驻；ORDER BY 关键字条→行式排序编辑浮层：字段/ASC/DESC/增删行；变更即时重查；单一「保存搜索」入口（ClickStack 式弹层：保存当前搜索 + 已保存列表，点击应用/删除，不再重复双按钮）；SELECT/ORDER BY 关键字条 flex 弹性拉伸（约 3:1）填满行 1 至右侧保存操作，触发器 w-full 撑满容器不按内容收缩，SQL 模式行 1 不换位）、行 2（ClickStack 式单行工具条、两种语法同构：查询语法 Lucene/SQL 切换 + 表结构 Popover + WHERE 定义栏（同位不同语法：Lucene = 空格分隔属性表达式自动编译为参数化 WHERE；SQL = 手写 WHERE 片段输入框；两者均为智能建议输入框（2026-11-18）——光标处解析当前词元（`resolveTokenContext`：方括号深度感知的冒号切分、词元边界与 tokenize 一致、容忍引号内空格与 NOT 前导 `-`），Lucene 模式裸词建议 JSON 根 + 物理列（ts 由时间选择器承载不进建议）、`field:` 后建议该字段 top-10 值分布（计数 hint，值含空白自动包短语引号，数值字段另给 > >= < <= 操作符候选）、JSON 括号路径前缀（`attributes["http"]["`）建议下一层子属性（服务端 json_keys 按绑定路径枚举，抽样口径同 facet，搜索词防抖 200ms）；SQL 模式建议物理列 + 常用关键字静态候选（不做值建议）；键盘 ↑/↓ 高亮、Enter/Tab 采纳候选（弹层打开时截断运行），无弹层 Enter 照旧运行，Esc 关闭，IME 组合输入期间不拦截任何键；值/子属性建议查询锚定最近一次执行时间窗，未执行时字段建议仍可用）；同行右侧依次为时间范围/Live Tail 合一触发器（紧凑自然宽度不拉通栏，相对预设显示 15m 式 key，Live Tail 激活态 accent 描边 + ⚡，弹层 align=center 锚定触发器）+ 定时刷新间隔选择器（关闭/1s/2s/4s/10s/30s：相对范围与 Live Tail 滑窗下按间隔轮询重查，自定义绝对区间自动停用置灰）+ 行数 + 运行（普通描边按钮，HyperDX 式不做底色强调））；时间范围弹层（ClickStack 式：头部 Live Tail 开关 + 1h back/1h forward 区间整体平移（即时生效）+ 关闭按钮；左栏 Live Tail 项（回看 15m 滑窗）+ 最近 1 分钟…最近 7 天相对范围列表（多久到现在），选中即以 now 为终点滑窗重查；右栏页签「Time range」（起止时间）/'Around a time'（时间点 ± 时长 Duration），输入框支持自然语言（昨天/1小时前/now 等，parseNaturalDate 解析，中英文）与标准 YYYY-MM-DD HH:mm，无法识别标红提示，Apply 确认（原「查询」按钮））；时间范围选择变更立即重查（预设/Live Tail/自定义 Apply 选完即查，不等 10s 定时轮询），连续快速切换时丢弃过期响应只落最后一次结果；**Lucene 模式（默认）**：属性搜索表达式（`field:value`、数值比较 `field:>=n`、前缀 `field:pre*`、`-排除`、裸词/`"短语"` 全文、AND/OR/括号、JSON 字面 key 路径 `attributes.order.no`（点分 key 单层直取，与后端 QueryDslCompiler 一致）、括号嵌套路径 `attributes["http"]["status_code"]`（逐层对象 key / 数组显式索引，与字面点分 key 互不串查））由前端 `lucene.ts` 编译为参数化 WHERE 片段（`$p0..$pN` + `Parameters` 字典服务端 DuckDBParameter 绑定；JSON 提取路径同为 `$jpN` 绑定 + json_valid 防护），显示字段/排序按「物理列白名单 + JSON 括号路径」校验——JSON 字段可作 SELECT 结果列（`json_valid` 防护提取表达式以显示名作带引号列别名，结果表列名即字段引用，列头排序/添加过滤器直接复用该引用）与排序字段；**SQL 模式**：行 2 手写 WHERE 片段（如 `severity = 'ERROR' AND service = 'api'`），与行 1 SELECT/ORDER BY 关键字条及行数上限组装为完整查询经 `/analytics/search` 执行（`{{logs}}/{{traces}}` 表占位符 + `$start/$end` 服务端参数绑定；整句自由 SQL 编辑器已移除，任意聚合分析 SQL 走 `/analytics` SQL 分析页；历史整句快照/旧地址 `sql` 参数打开时回退提取 WHERE 主体）；左侧分析面板（与主区留白分隔、可整面板折叠成窄条；分析模式：结果表/事件模式占位，选中项 accent 左描边；过滤器区可折叠：Denoise 抑制噪声排除 DEBUG/TRACE + HyperDX 式属性过滤器树——分组 ResourceAttributes/日志属性/日志体（JSON，body 为合法对象/数组时可浏览 key）/服务名/日志级别/来源（traces 为 SpanAttributes/Span 名称/Span 类型/状态），JSON 组显示 {n} distinct key 数、物理组显示 (n) distinct 值数，key 行 font-mono 名称 + (count) + 常显图标 ⊕/⊘ 生成 exists/nexists 键存在性过滤（物理列组头同样常显，操作图标一律不随 hover 显形，含义经 Tooltip），JSON key 行另有常显 {} 「子属性」按钮——逐层懒加载枚举嵌套 key（json_extract 按绑定路径取节点 + json_keys 枚举，与 facet 同抽样/搜索口径），嵌套 key 以括号路径形态递归复用 key 行交互（值分布/存在性过滤/继续展开），key 展开懒加载值列表初始 top-10 值分布 + 值搜索框（服务端 ILIKE $key 参数化过滤、防抖 200ms，属性组 key 枚举同样可搜索，物理列组仅组头一个搜索框，搜即所得不必枚举）+ Load more（每次 +50 提高 LIMIT 重查非分页，组 key 枚举同样初始 10 条可续载，避免侧栏一次性展示过多），值行三态复选框：勾选并入 in 过滤（组内 OR）、半选排除并入 nin（值文本删除线）、未选无过滤，行尾常显文字操作「仅此值（HyperDX Only，清空该字段仅留此值）/排除（取消排除）」（文案自明操作，替代 hover 图标猜测）；facet 数据经 /analytics/search 直查通道聚合（与直方图同构）：json_valid + unnest(json_keys()) 枚举属性 key、json_extract_string 经 json_valid 防护 + 绑定路径（$jpN）取值 top-N（点分字面 key 与括号嵌套路径同一链路），聚合为 ClickStack 式行抽样统计（默认列表 USING SAMPLE 1000 行 reservoir 采样，本仓库 DuckDB 不接受 ROWS 后跟种子参数——语法经 Store.Tests 真实执行实证，计数为样本估计值而非全量精确统计，避免高基数属性全量 GROUP BY 扫描；搜索态查询不加抽样，ILIKE 扫描整个搜索时间窗，样本外稀有值可搜），搜索词经 LIKE 字面转义（\ % _ + ESCAPE）与 $key 参数绑定不参与 SQL 组装，时间窗取最近一次执行范围（与 HyperDX 面板口径一致：DBSearchPageFilters 的查询锚定 chartConfig.dateRange 搜索窗口，不放大）、denoise 条件与主查询一致，不随 filters 变化重查避免反馈环；过滤器 chips 兼容 in/nin/exists/nexists 文案可随时手动移除；「更多过滤器」底栏切换属性过滤器树显隐（默认收起避免常驻占据侧栏，展开后底栏变「收起过滤器」并附「没看到想要的过滤器？试试直接搜索（如 service:api）」提示，手动过滤走搜索栏 Lucene 表达式或结果列菜单「添加为过滤器」，早期手动内联表单已移除）；表结构 Popover（Lucene 模式点击字段追加表达式、SQL 模式插入光标处）；常驻直方图（Lucene 模式服务端全量聚合分桶：同一 WHERE 条件经 `/analytics/search` 并行聚合查询（`epoch_ms(ts)//步长×步长` 整刻度分桶，步长按时间窗自适应 60~120 桶（ClickStack 式密度），步长经 `$bucket_ms` 参数绑定，rowLimit 10000，失败自动回退），x 轴全量零填充空桶保持时间位置与密度正确，结果头部计数为范围内匹配精确总数（如 3,038 条结果，不受行数上限影响）；聚合查询失败回退返回行客户端分桶（计数为返回行数，截断 + 标注）；severity/status 堆叠，平铺无框细柱（barMaxWidth 14）、少量 y 轴刻度（千位 k 格式化）无网格，x 轴线即分隔线，无数据空时间轴，点击柱形缩放到该时间桶；Chart 封装显式 height 时不再叠加默认 minHeight 200，杜绝溢出容器压住表格）；结果头部（N 条结果/耗时 + 原始 JSON 视图（{} 图标切换，前 500 行，顶部细线分隔）与表格视图切换 + `</>` 生成 SQL 弹层（HyperDX 式 Generated SQL：最近一次实际执行的查询 SQL 与直方图 SQL 分节展示，只读 CodeMirror 高亮 + 逐节复制按钮）+ CSV 导出（RFC 4180 转义 + BOM））+ 动态列结果表（无分页，行数由 rowLimit 控制；table-fixed 高密度单行——固定宽度列锚点、其余列均分剩余宽度、单元格 truncate 不换行；表头与页面同层 bg-bg、列名间细竖线 + 列菜单：升/降序重查、添加为过滤器；行首展开箭头列；ts 毫秒精度显示（YYYY-MM-DD HH:mm:ss.SSS）；ts/severity/status/duration_us/trace_id 语义渲染，severity/status 为着色纯文本（ERROR 红/WARN 橙/INFO 灰/DEBUG 蓝/TRACE 淡）非 Tag 药丸）；行悬浮行尾浮现「复制整行 JSON」按钮；行点击打开 ClickStack 式详情抽屉（右侧滑出至少半屏宽 `max(50vw, 640px)`，四页签：概览——消息体 + 顶层属性/日志属性键值列表（各带按键/值搜索框，悬浮复制，trace_id 可点击）+ 资源属性 chip 行（span 记录改为 Span 摘要 + Span 属性 + 事件 + 资源属性）；列值——完整记录全字段键值网格 + 按值过滤（JSON 列递归解析为可折叠树，根容器 `{n} keys` HyperDX 式可收展，子节点递归、叶子按类型着色）；链路日志——同 trace_id ±1h 窗日志列表（点击条目切换抽屉目标）；上下文——可调时间窗与匹配条件的分批邻域日志（仅日志记录，详见 [logs 设计](logs/sdd.md)））；抽屉打开时按「ts + service（+severity/trace_id/span_id 在场即并入）」身份条件经 `/analytics/search` 补查完整记录（SELECT 全列含 attributes/resource，结果表行仅含所选列），失败回退展示行数据并提示重试；行首箭头保留就地展开明细并支持三预览切换（字段——平铺 + 按值过滤联动，默认；JSON——整行对象递归折叠树；原文——body 不做任何解析直接展示）；trace_id 点击按域分流：日志域深链跳转链路视图 `/search?source=traces&traceId=…`（push 历史栈可回退、刷新保留、落地为 trace_id eq 过滤器查询），链路域内点击开瀑布图弹层；HyperDX 式空态（整幅浅灰块：未找到匹配结果 + 语法提示 + 实际搜索时间范围）；保存搜索 definition 存 domain/mode/where/fields/orderBy/limit/rangeMs（历史 sql/query 兼容打开）；执行通道统一 `POST /api/analytics/search`（只读单语句 + token 展开 + 命名参数绑定）；**URL 条件同步（2026-11-17）**：查询条件（source/语法模式/Lucene q/SQL sql/字段/排序/过滤 chips/降噪/时间 range 预设或 from+to 自定义区间/行数/刷新间隔）经 `searchUrlState.ts` 单向投影到地址栏——非默认条件以 replace 实时写入（不产生历史记录），复制地址重新打开自动恢复并重查（非法值安全回落默认），既有 traceId 深链参数透传保留，savedId 上下文（/saved-searches/{id}）不同步；旧 `/logs` `/traces` 带 source 重定向；**数据域切换条件适配（2026-11-18 修正）**：切换 logs↔traces（数据源选择器 / traceId 深链 / 浏览器回退还原 URL 三条路径）视为上下文变更——SELECT 字段与排序无条件重置为新域默认（跨域字段列表整体失效、没有可移植子集；SQL/Lucene 两种语法共用行 1 关键字条，适配不能只覆盖 Lucene——修复前 SQL 模式整体跳过适配、且自定义字段跨域残留，编译报「未知字段 severity」横幅零结果）、属性过滤器跨域清空、详情抽屉关闭，重置后即时以新域条件重查；手写 WHERE 表达式（Lucene q / SQL 片段）为用户显式输入保持不动，跨域非法引用仍经编译校验以错误横幅提示；savedId 快照上下文由快照驱动不受切域适配影响 |
| 图表探索 | `/charts` | 扩充（2026-11-18，Time Series 对齐 HyperDX） | HyperDX Chart Explorer | HyperDX 式 Builder 图表页（全等宽字体）：9 类页签（下划线高亮式，时序/表格/数值/柱状/饼图/事件搜索/热力图已实现，模式/Markdown 占位）、Builder/SQL 双模式（Builder 由序列行生成 SQL：聚合函数+条件+Group By+粒度+时间范围，多序列 UNION ALL；每序列行含别名 + 复制序列；SQL 模式 CodeMirror 直写并继承上次参数字典）、数据源切换（logs/metrics/sessions/traces/iot，MQTT（设备遥测）内部使用 iot，包含 HTTP 遥测，设备台账仍独立；Sessions 为客户端 RUM 会话，窗口内按项目与会话去重）、Run 旁操作菜单保存到仪表盘（保留参数、展示配置与现有布局，并发修改返回冲突）、添加序列/添加公式（占位）/显示设置（图例开关）、右侧 **TimeRangePicker 完整时间范围**（相对预设 + 自定义起止（自然语言）+ 某时间前后 + 1h 平移；图表页隐藏 Live Tail；相对预设每次运行锚定 now，绝对区间固定不变）+ 自动粒度（按实际跨度计算）+ 运行、时序区折线/柱形显示切换 + **真实时间坐标轴**（不隐式堆叠、缺失桶断线不补零）、折叠区「匹配事件样本」（并行查询 500 行：行首箭头就地展开字段/JSON/原文三预览 + 按值过滤，点击行打开日志详情抽屉（身份补查/五页签，复用搜索页组件），trace_id 点击开瀑布图；按值过滤生成 `$pN` 参数绑定条件 chips 并自动重查；样本失败显式报错不吞掉，SQL 模式说明不可用原因）与「生成的 SQL」（只读 CodeMirror 高亮 + 复制 + 参数字典展示）；执行复用 `POST /api/analytics/search`（只读单语句 + token 展开 + 参数绑定）；图表/样本/SQL 以执行快照渲染（防旧请求覆盖与编辑后误读） ；**事件搜索**为独立 SELECT/条件/排序/时间工具栏，日志与链路高密度事件表、详情与参数化过滤、固定执行快照续页；不展示聚合或重复样本区，详见 [analytics](analytics/sdd.md) |
| 已保存搜索 | `/saved-searches`、`/saved-searches/{id}` | **新增**（2026-11-17） | HyperDX Saved Searches | 搜索页保存的命名快照列表（saved_query `kind=search`），菜单位于「搜索」之下（Bookmark 图标，子路径前缀高亮）：名称筛选 + 卡片网格（数据源标签 logs/traces + 快照预览（Lucene 表达式/SQL WHERE 片段，历史整句回退首行）+ 更新时间）+ 打开/删除（写角色 Popconfirm）+ 空态引导 +「新建搜索」入口（回 /search）；打开 = **同一 `SearchPage` 组件注入 `savedId` prop**（零复制，搜索页后续变化自动同步）：经 `GET /analytics/queries/{id}` 加载快照，恢复 domain/语法模式/where/字段/排序/行数/时间窗并自动查询，保存按钮旁 Tag 显示当前条目名，弹层列表点击其它条目 = 切换打开，删除当前条目回列表；上下文中保存动作 = 「更新当前搜索」（POST Id≠0 覆盖 Definition，不新建），`/search` 页保存仍为新建（Id=0）；快照条件恢复后经 pending-run 机制以恢复完的状态重查（避免过期闭包首查） |
| 指标 | `/metrics` | 已有，扩充 | OpenObserve Metrics | 指标名浏览/搜索、labels 展示、聚合（rate/avg/max）+ 分组维度 + 多序列时序图 |
| 设备 | `/iot` | 已有，扩充 | —（IoTX 类） | 设备台账 CRUD、遥测曲线与最新值；搜索/查询/新建同一左对齐工具条，管理员可设置免key、统一key或key前缀的 MQTT 接入（均需已注册启用设备，见 iot 文档），不承诺在线状态 |
| Client Session | `/sessions`（旧 `/rum` 兼容） | 重构（2026-11-19） | HyperDX Client Sessions | 项目/搜索/时间统一工具栏、服务端会话分页与筛选、会话详情/事件时间线/rrweb 回放、深链恢复、接入引导；性能/热力/实时/站点地图为辅助视图，详见 [RUM 设计](rum/sdd.md) |

### 服务地图

- `/service-map`，位于图表探索之后，admin/write/readonly 均可查询，无写入功能。
- 服务筛选、SQL WHERE/Lucene、Trace 级采样、时间范围与时间桶缩窗、有向依赖与真实 Trace 钻取；设计和验收边界见 [traces SDD](traces/sdd.md) 与 [TDD](traces/tdd.md)。

### 可视化

| 页面 | 路由 | 状态 | 对标 | 核心功能 |
|---|---|---|---|---|
| 仪表盘 | `/dashboards`、`/dashboards/:id`、`/dashboards/:id/panels/new`、`/dashboards/:id/panels/:panelId/edit`、`/dashboards/:id/panels/:panelId/preview` | 已有，扩充 | HyperDX 交互 / Grafana 分层 | 自由画布与整盘草稿保存；独立探索及面板编辑共用无路由依赖的 ChartWorkspace，查询、展示和编辑恢复协议分离，兼容旧配置；查询复用 metric/DSL/PromQL/SQL 执行器，详见 [dashboard](dashboard/sdd.md) |
| SQL 分析 | `/analytics` | 已有 | OpenObserve SQL 编辑器 | 只读 SQL（IAnalyticsExecutor）+ 保存查询 + 行数上限提示 |

### 接入

| 页面 | 路由 | 状态 | 对标 | 核心功能 |
|---|---|---|---|---|
| 数据接入 | `/ingestion` | **新增** | ClickStack/OpenObserve Ingestion | 各协议接入指南 Tab（OTLP/ES Bulk/MQTT/RUM/自有 JSON）+ 复制即用配置片段 + 通道状态（端口/启用/最近写入） |
| 接入 Key | `/keys` | **新增（仅 admin）** | ClickStack Ingestion Keys | IngestKey 增删停用、Key 仅创建时显示一次、最近使用时间、审计列 |
| RUM 项目 | `/rum-projects` | **新增** | OpenObserve RUM Setup | 项目键（公开）、域名/CORS、采样率（性能/回放分开）、今日 PV |

### 日志消费（/log-consumption）

默认 Status 展示实例和配置主题规则的状态、60 秒消息/记录/字节速率、解析质量与确认结果；前缀匹配只汇总数量，不展开物理队列。Kafka/RabbitMQ/Pulsar 三个页签保留原配置详情与编辑交互。实时与 DuckDB 历史采样的口径及验证边界见 [monitor](monitor/sdd.md) 和 [验证记录](monitor/tdd.md)。

| 页面 | 路由 | 状态 | 核心功能 |
|---|---|---|---|
| 日志消费 | `/log-consumption` | 已实现（2026-11-17 多实例重做） | MQ 多实例接入管理（Kafka/RabbitMQ/Pulsar 可建任意多实例：实例卡片列表含类型 Tag/启用开关/连接摘要/topic 概览，开关即存、删除 Popconfirm、新建/编辑抽屉按类型动态连接表单）；每 topic 解析规则 8 种（otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw）+ 规则参数（正则捕获组/分隔符列索引/字段映射/时间格式）+ 「测试解析」样例试跑预览；保存全量替换（`system:mq`），消费管理器 10s 内自动热更新无需重启；仅管理员可见；原「系统设置 → MQ 日志源」tab（2026-11-16 迁出）+ 旧单实例三段卡片升级为多实例形态 |

### 系统设置（/settings，tab 结构）

- 通知渠道（2026-11-19）：管理员访问 `/settings?tab=notifications`，支持 11 类渠道、多实例、启停、审计、凭据保护及确认后测试发送；未来 Alert 通过 Shared 发送接口按渠道 ID 使用。详见 [settings 设计](settings/sdd.md)。

| 页面 | 路由 | 状态 | 核心功能 |
|---|---|---|---|
| 系统设置 | `/settings` | 已实现（2026-11-16） | 顶部 Tab：成员（全员，默认激活；服务状态统一在 Overview 展示，原「用户管理」独立页并入：新建/改权限/重置密码/删除仅管理员、成员可改自己密码）、冷存储 S3（连接测试/密钥掩码/保存立即重建 SECRET）、数据保留（5 域；已启用 S3 时仅冷存天数，本地转冷天数在 S3 页编辑；未启用时显示完整策略）、数据压缩（归档 parquet 编码）、自身遥测 OTLP（重启生效）；管理员 Tab 按角色显隐，`?tab=` 深链；旧 `/users` 路由重定向到成员 tab；MQ 日志源 tab 已迁出至「日志消费」（`/log-consumption`）；文档 [settings/](settings/sdd.md) |

## 权限 × 页面矩阵

| 页面 | 只读 | 读写 | 管理员 | 操作差异 |
|---|---|---|---|---|
| 观测 6 页 | ✓ | ✓ | ✓ | 全员可查；写按钮（保存查询/添加设备等）按写权限显隐 |
| 仪表盘/SQL | ✓ | ✓ | ✓ | 保存/新建/编辑按写权限显隐 |
| 数据接入 | ✓ | ✓ | ✓ | 全员可看指南与通道状态 |
| RUM 项目 | ✓ | ✓ | ✓ | 新建/改采样按写权限 |
| 接入 Key | ✗ | ✗ | ✓ | 仅管理员整页可见 |
| 日志消费（MQ 日志源） | ✗ | ✗ | ✓ | 仅管理员可见（非管理员展示提示） |
| 系统设置（成员 tab） | ✓ | ✓ | ✓ | 全员可见（团队共用）：成员仅可改自己密码；新建/删除/重置密码/改权限仅管理员 |
| 系统设置（存储/保留/压缩/OTLP/摄取 tab） | ✗ | ✗ | ✓ | 仅管理员可见 |

## 关键交互约定

- 数值页签（Number）：大数居中、隐藏分组与粒度；支持八类输出格式、序列/公式格式继承与覆盖、首匹配条件色和可选背景趋势，保存至仪表盘保持一致。设计与验证边界见 [analytics 设计](analytics/sdd.md) 和 [验证记录](analytics/tdd.md)。

- Trace 详情与 Surrounding Context（2026-11-18）：概览/主时间轴联动、µs/ms/s 耗时、日志/事件详情，以及按服务/主机/属性查询周边 traces/logs；具体语义与验证边界见 [traces 设计](traces/sdd.md) 和 [验证记录](traces/tdd.md)。

- 全局时间范围在顶栏，页面内可覆盖；日志⇄链路经 trace_id 互跳；RUM 会话与 JS 错误时间轴对齐。
- 搜索页查询条件同步到地址栏（query 参数非默认写入、复制链接打开自动恢复；2026-11-17 起，其它页面后续按同一 `searchUrlState` 模式接入，见 analytics/sdd 决策）。
- 实体表展示审计列（`AuditCell`：时间在上、操作人在下，既有规范）。
- 删除类操作 Popconfirm 二次确认（既有规范）。
- 新增页面逐一补 `__tests__` 冒烟 + i18n 双语 key（既有规范）。

## 后端接口增量（支撑本设计，映射 architecture.md）

- `/api/rum/*`：性能汇总、会话列表、回放分段、热力栅格、站点地图（M6）。
- `/api/keys`：即现有 IngestKey CRUD 扩充「最近使用时间」。
- `/api/rum-projects`：RUM 项目 CRUD（M6）。
- `/api/dashboards`：仅支持普通仪表盘（view）；Screens 已下线，历史 screen 不提供活动接口，不自动删除或转换。
- `/api/ingestion/status`：通道启用状态/端口/最近写入时间聚合（M1/M2/M4 汇总展示）。
- `/api/settings`：保留策略与冷存储配置读写（M5，已实现：GET 合并视图 + PUT s3/retention/compression/mq/otlp + POST s3/test，仅管理员）。
