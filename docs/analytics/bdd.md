# analytics — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 只读分析查询

  @ANALYTICS-S220 @manual
  Scenario: 从完整搜索条件创建告警
    Given 搜索包含侧栏过滤和降噪条件
    When 用户保存搜索与告警
    Then 告警冻结全部有效条件且搜索后续编辑不会静默改变规则

  @ANALYTICS-S210 @auto:xunit
  Scenario: 工作台浏览实际表结构
    Given DuckDB 中存在观测数据表
    When 用户刷新目录并选择一张表
    Then 展示实际字段类型与可空性且不覆盖查询草稿

  @ANALYTICS-S211 @auto:xunit
  Scenario: 工作台使用简易属性查询
    Given 用户选择日志、链路、指标、遥测或会话来源
    When 输入带括号和布尔运算的属性条件并运行
    Then 条件值与时间安全绑定且执行遵守行数上限

  @ANALYTICS-S212 @auto:xunit
  Scenario: 工作台保存与恢复
    Given 用户已编辑查询模式和参数
    When 保存工作台后重新载入
    Then 恢复查询定义且不自动执行，不混入统一搜索快照

  @ANALYTICS-S214 @auto:xunit
  Scenario: 表与字段独立筛选
    Given 用户已选择一张表
    When 分别筛选表名与字段名并切换表
    Then 两个筛选互不影响且切换表会清空字段筛选

  @ANALYTICS-S215 @manual
  Scenario: 工作台使用紧凑查询栏
    Given 用户进入分析工作台
    When 切换查询语言
    Then 模式选择保持固定宽度且查询条件与运行控件紧凑排列，草稿保持不变

  @ANALYTICS-S213 @manual
  Scenario: 工作台补全与响应式布局
    Given 用户进入分析工作台
    When 使用键盘补全和光标插入并切换主题与窄屏
    Then 表名与字段建议正确且编辑区和结果保持可用

  @ANALYTICS-PIE-SOURCES @auto:xunit
  Scenario: 饼图按四种来源配置分类统计
    Given 用户选择日志、链路、指标或客户端会话来源
    When 配置来源适用的聚合和分类并运行
    Then 指标限定所选名称且标签条件安全绑定，链路支持耗时分位数，会话按项目和会话去重

  @ANALYTICS-PIE-VALIDATION @manual
  Scenario: 饼图拒绝不可用配置并保留来源草稿
    Given 用户已配置指标名称与标签分类
    When 切换来源后返回并尝试不适用的聚合或分类
    Then 原配置保留且无效配置在请求前明确报错，不混合不同指标或旧来源结果

  @ANALYTICS-S194 @manual
  Scenario: 生成查询展示实际条件值
    Given 用户已使用包含文本、数字或空值的条件运行图表
    When 展开生成查询或预览并复制
    Then 查询直接展示正确转义的实际条件值，执行和保存仍安全绑定参数

  @ANALYTICS-S191 @manual
  Scenario: 四类探索视图隔离草稿和执行结果
    Given 用户打开饼图、事件搜索、热力图或模式页签并执行查询
    When 修改草稿或在查询返回前切换来源
    Then 未运行修改有明确提示且旧来源结果不再展示，返回原来源仍保留草稿

  @ANALYTICS-S192 @manual
  Scenario: 饼图和热力图从匹配样本继续过滤
    Given 用户已运行饼图或热力图并编辑了尚未运行的条件
    When 从匹配样本详情选择按值过滤
    Then 恢复已执行条件与绝对时间并安全绑定过滤值，样本失败不隐藏成功图表

  @ANALYTICS-S193 @manual
  Scenario: 模式下钻后再次运行保持条件
    Given 用户已查看模式的代表事件
    When 按字段值过滤后再次运行或保存重开
    Then 原始条件与结构化过滤保持一致且时间不漂移，会话模式明确按事件计数

  @ANALYTICS-S50 @auto:xunit
  Scenario: 分类柱状图统计完整时间范围
    Given 不同类别在多天产生匹配事件
    When 按类别聚合并取最大的若干类别后按名称排序
    Then 展示完整时间范围的分类统计且不生成时间桶，筛选值安全绑定

  @ANALYTICS-S55 @manual
  Scenario: 时序图与柱状图共用匹配事件样本
    Given 用户已运行时序图或分类柱状图
    When 展开样本、查看事件详情或按字段值过滤
    Then 两种图表提供一致的样本交互，过滤沿用执行时来源与时间并安全绑定，未运行草稿不混入查询

  @ANALYTICS-S54 @manual
  Scenario: 柱状图坐标轴与分类带宽
    Given 用户运行单分类或多分类柱状图
    When 查看坐标刻度、分类标签和数值提示
    Then 数值轴自动缩写单位且提示保留配置精度，柱宽随分类带宽变化，分类标签水平展示且长文本不会遮挡相邻分类

  @ANALYTICS-S53 @manual
  Scenario: 柱状图聚合列输入
    Given 用户打开柱状图并选择事件计数
    When 切换求和并输入字段或数值表达式后运行，再切换计数后返回
    Then 计数不显示列输入框，其它聚合显示可编辑列及字段提示，表达式参与查询且草稿保留

  @ANALYTICS-S52 @manual
  Scenario: 柱状图条件语法与智能提示
    Given 用户打开柱状图条件输入框
    When 切换属性语法和查询语法并输入字段或条件
    Then 各语法草稿独立保留，属性语法提示字段与实际值，查询语法提示字段与关键字，采纳建议不执行查询

  @ANALYTICS-S51 @manual
  Scenario: 分类柱状图配置与显示
    Given 用户在柱状图配置单个聚合与多个分组字段
    When 切换页签返回或修改数值格式与分类上限
    Then 草稿保持且格式应用仅重绘，分类上限在再次运行后生效，无序列复制和时间粒度入口

  @ANALYTICS-S182 @manual
  Scenario: 时序查询数据来源联动
    Given 用户已编辑日志来源的查询与绑定参数
    When 切换指标、客户端会话、链路并切回日志
    Then 查询编辑区和示例跟随来源，各来源草稿与参数独立保留，旧结果清空且点击运行后才查询

  @ANALYTICS-S180 @manual
  Scenario: 时序图查询编辑工作区
    Given 用户在时序图构建器中配置了序列和公式
    When 切换到查询编辑模式并展开或折叠图表说明
    Then 展示可复制示例、大编辑区和运行工具条，不显示构建器专属控件，切回后配置仍保留

  @ANALYTICS-S181 @manual
  Scenario: 查询草稿与参数保留
    Given 用户从带绑定参数的构建器查询进入查询编辑模式
    When 修改草稿并切换模式返回后使用运行快捷键
    Then 仅执行一次编辑后的查询且绑定参数保留，修改显示设置不触发重查

  @ANALYTICS-S90 @manual
  Scenario: 饼图单序列聚合与独立配置
    Given 用户打开饼图页签
    When 配置聚合、条件和可选分组并运行后切换页签返回
    Then 饼图展示实际聚合结果且保留编辑状态

  @ANALYTICS-S91 @manual
  Scenario: 饼图显示设置确认生效
    Given 饼图已有运行结果
    When 修改分类上限和数值格式并点击应用
    Then 仅显示最大的分类且按配置格式展示数值，取消修改不影响结果

  @ANALYTICS-S73 @manual
  Scenario: 聚合条件双语法与智能提示
    Given 用户在数值或时序构建器配置多个输入序列
    When 逐序列选择查询语法或属性语法并采纳字段与值建议后运行
    Then 采纳不触发查询，属性条件值安全绑定且序列参数互不覆盖，主值和趋势沿用同一条件

  @ANALYTICS-S188 @manual
  Scenario: 属性提示连续发现路径与值
    Given 用户切换数据源并尚未运行图表
    When 输入属性根路径并采纳子属性
    Then 使用当前时间范围展示候选并提供值筛选和继续下钻入口，失败可重试且空结果明确提示

  @ANALYTICS-S70 @manual
  Scenario: 数值页签显示整段时间聚合
    Given 用户已配置聚合条件
    When 切换数值页签并运行
    Then 隐藏分组和时间粒度并居中展示大数，零值与空值明确区分

  @ANALYTICS-S71 @manual
  Scenario: 数值格式继承与条件颜色
    Given 数值图表包含输入序列和公式
    When 应用图表格式并自定义单个序列格式
    Then 展示立即更新而不重查主值，条件颜色按最后匹配规则生效且重排改变优先级

  @ANALYTICS-S72 @manual
  Scenario: 数值背景趋势独立加载
    Given 构建器数值图表已经运行
    When 开启背景趋势或趋势查询失败
    Then 趋势沿用执行时间与序列标识，缺失时段不补零且固定区间重跑也刷新，失败不影响主数值

  @ANALYTICS-F1 @manual
  Scenario: 输入序列计算公式并切换展示
    Given 图表已配置输入序列 A 和 B
    When 添加公式并输入 A 除以 A 与 B 之和再乘以一百后运行
    Then 按相同时间桶和分组计算结果，关闭显示输入序列后仅展示公式结果

  @ANALYTICS-F2 @auto:xunit
  Scenario: 公式拒绝无效引用且空值不补零
    Given 公式包含未知输入或缺失操作数
    When 校验表达式或执行计算
    Then 未知引用阻止查询，缺失操作数和除零显示为空值

  @ANALYTICS-S29 @manual
  Scenario: 已保存搜索工具条左对齐
    Given 用户进入已保存搜索列表
    When 查看列表上方工具条
    Then 名称筛选框和新建搜索按钮按此顺序靠左排列，不贴靠页面右侧

  @ANALYTICS-S20 @manual
  Scenario: 侧边菜单优先展示搜索分析入口
    Given 用户已进入平台
    When 查看侧边菜单
    Then 前五项依次为总览、搜索、已保存搜索、图表探索、仪表盘，其余菜单保持原有相对顺序

  @ANALYTICS-MENU-I18N @manual
  Scenario: 侧边菜单随界面语言切换
    Given 用户使用简体中文界面
    When 查看侧边菜单
    Then 对应入口显示为仪表盘和客户端会话
    When 将界面语言切换为英语
    Then 对应入口显示为 Dashboards 和 Client Sessions

  @ANALYTICS-S18 @manual
  Scenario: 搜索列表触底续页与结束
    Given 已成功查询日志或链路
    When 滚动到结果列表末尾
    Then 有更多数据时自动追加下一批，保留已有行与滚动位置
    Then 没有更多数据时显示结束标识，加载中或失败时不显示结束标识

  @ANALYTICS-S19 @manual
  Scenario: 搜索续页保持查询上下文且无截断文案
    Given 已查询结果仍有更多数据
    When 在中文或英文界面滚动续页
    Then 不显示截断或显示上限提示，续页沿用已执行的条件与时间范围
    Then 续页失败保留已有数据并允许重试，重新查询后旧续页响应不再混入

  @ANALYTICS-S1 @auto:xunit
  Scenario: 合法 SELECT 正常执行
    When 执行 "SELECT 1 AS one"
    Then 返回列 one 与 1 行数据

  @ANALYTICS-S2 @auto:xunit
  Scenario: 写操作与多语句被拒绝
    When 执行 "DELETE FROM logs_x" 或 "SELECT 1; SELECT 2"
    Then 返回 400 及拒绝原因

  @ANALYTICS-S3 @manual
  Scenario: 保存查询可再次执行
    When 保存一条查询并在列表中重新执行
    Then 返回相同结构的结果

  @ANALYTICS-S4 @manual
  Scenario: 统一搜索保存的搜索（kind=search）
    When 保存 kind=search 与 definition JSON（domain/mode/where/fields/orderBy/limit/rangeMs，统一搜索页「保存搜索」；历史 sql/query 格式仍可打开），列表按 kind=search 过滤
    Then 持久化不含 SQL 文本、执行通道对 kind=search 返回 400（无 SQL 可执行）；旧 kind=sql 行为不变

  @ANALYTICS-S13 @manual
  Scenario: 按 id 查询保存项
    When GET /api/analytics/queries/{id}（仅需登录）
    Then 返回该保存项（含 Name/Kind/SqlText/Definition/Description/审计时间）；不存在返回 404；已保存搜索页打开 `/saved-searches/{id}` 时经此通道加载快照

  @ANALYTICS-S14 @manual
  Scenario: 已保存搜索更新语义（保存即更新）
    When 已保存搜索上下文（/saved-searches/{id} 打开的同一 SearchPage 组件）修改查询条件后点「更新当前搜索」
    Then POST /api/analytics/queries 携带原 id（Id≠0 更新分支）覆盖 Definition，不新建条目；/search 页直接保存仍为新建（Id=0）；更新与新建均限 AdminOrWrite 角色

  @ANALYTICS-S15 @manual
  Scenario: 统一搜索页查询条件同步到地址栏（UI）
    When 在 /search 设置查询语句、语法模式、显示字段、排序、过滤 chips、降噪、时间范围、行数、定时刷新并执行查询
    Then 地址栏 query 参数实时反映全部非默认条件（source 恒写入兼容 /logs /traces 重定向；q/mode/sql/fields/orderBy/filters/denoise/range 或 from+to/limit/refresh 非默认才写入），replace 更新不产生历史记录；既有 traceId 深链参数原样透传；视图偏好（表格/JSON、事件模式）与瀑布图弹层不入 URL

  @ANALYTICS-S16 @manual
  Scenario: 带条件地址打开自动恢复（UI）
    When 打开携带条件参数的 /search 地址（含非法值：未知 range 预设、越界 limit/refresh、损坏 filters JSON、to 早于 from）
    Then 页面按参数恢复数据源/模式/语句/字段/排序/过滤器/时间窗/行数/刷新间隔并自动执行首查；非法值安全回落默认（不报错不阻断）；savedId 上下文（/saved-searches/{id}）不做 URL 同步（快照驱动语义不变）

  @ANALYTICS-S17 @manual
  Scenario: 浏览器回退跨数据域还原（UI）
    When 日志视图点 trace_id 深链跳转链路视图后按浏览器返回键（回退只还原 URL 不还原组件状态）
    Then 渲染期域适配让上一域默认字段/排序跟随新数据域、跨域过滤器清空并以 logs 条件重查（不报 Unknown field name、地址被同步 effect 清洗为干净条件）；选择器手动切域与深链跳转走同一适配路径（已自定义的字段/排序保持不动，非法值以错误横幅提示）

  @ANALYTICS-S5 @auto:xunit
  Scenario: 搜索 SQL 表占位符展开与时间参数绑定
    When POST /api/analytics/search 执行含 {{logs}} 与 $start/$end 的 SQL（区间内已有按天表与数据）
    Then 占位符展开为区间内现存天表 UNION ALL BY NAME（按列名合并，兼容新旧天表列序差异），$start/$end 以参数绑定生效，返回过滤后的行

  @ANALYTICS-S6 @auto:xunit
  Scenario: 搜索 SQL 只读约束
    When 执行 "DELETE FROM logs_x" 或 "SELECT 1; SELECT 2"
    Then 返回 400 及拒绝原因（与 ANALYTICS-S2 同一校验）

  @ANALYTICS-S7 @auto:xunit
  Scenario: 未知表占位符与跨度超限拒绝
    When 执行含 {{rum}} 的 SQL 或时间跨度超过 MaxQueryDays
    Then 返回 400 及修正提示

  @ANALYTICS-S8 @auto:xunit
  Scenario: 区间内无现存天表短路
    When 时间区间内 {{logs}} 无任何现存按天表
    Then 直接返回空结果，不执行 SQL

  @ANALYTICS-S9 @auto:xunit
  Scenario: 搜索命名参数绑定（$p0..$pN）
    When POST /api/analytics/search 执行含 $p0/$p1 引用的 SQL 并携带 parameters 字典（如 severity = $p0、body LIKE $p1）
    Then 命名参数以 DuckDBParameter 绑定生效（字符串/数值/布尔/null），值含写关键字（如 delete）不触发只读黑名单

  @ANALYTICS-S10 @auto:xunit
  Scenario: 缺失命名参数拒绝
    When SQL 引用 $p0 但 parameters 未提供该键（或区间内无天表短路之外的缺失场景）
    Then 返回 400「缺少查询参数 $p0」，SQL 不执行

  @ANALYTICS-S183 @manual
  Scenario: 搜索语法说明与智能提示并存
    Given 用户在日志或链路搜索中使用属性语法
    When 编辑条件并展开或收起使用说明，再切换界面语言与查询语法
    Then 说明默认展开并展示当前编辑条件、支持的示例及字段存在性入口，操作不改写条件也不运行查询，查询语法模式不展示该说明

  @ANALYTICS-S11 @manual
  Scenario: 统一搜索页 Lucene 属性搜索（UI）
    When 在搜索页 WHERE 搜索栏输入属性表达式（field:value、数值比较、前缀 *、-排除、裸词全文、OR、括号、JSON 路径 attributes.xx）
    Then 前端编译为参数化 WHERE 片段（$p0..$pN）拼入搜索 SQL 经同一通道执行；未知字段/语法错误以人类可读中文提示且不发起请求；字段与排序输入按表结构白名单校验

  @ANALYTICS-S12 @auto:xunit
  Scenario: 跨列序不同的天表查询按列名合并（回归）
    When 时间区间同时覆盖旧版迁移天表（id/source 列追加在末尾）与新版天表（id 在首列）
    Then {{logs}} 展开为 UNION ALL BY NAME 按列名合并，查询正常返回两天的行（不再按位置错位抛 BIGINT→TIMESTAMP 转换异常导致 500）

  @ANALYTICS-S21 @auto:xunit
  Scenario: 图表探索页相对时间每次运行锚定当前、绝对区间保持固定
    Given 图表探索页选择相对预设（如最近 1 小时）
    When 连续两次点击运行
    Then 第二次的查询终点晚于第一次（相对窗口随 now 前移），窗口长度保持 1 小时

  @ANALYTICS-S22 @auto:xunit
  Scenario: 图表探索页自定义时间范围校验与自动粒度
    When 在时间范围弹层输入自定义起止区间（结束早于开始先被拒绝），应用后运行
    Then 查询使用输入的绝对起止时间（重复运行不漂移），自动粒度按区间实际跨度选择（48 小时 → 30 分钟）

  @ANALYTICS-S23 @auto:xunit
  Scenario: 匹配事件样本按值过滤走参数绑定并自动重查
    Given 图表探索页样本区展示匹配事件
    When 行首箭头就地展开明细（不打开抽屉）、点击行打开详情抽屉、在明细中对字段按值过滤
    Then 新查询 SQL 以 $p0 等命名参数携带该值（SQL 文本不含字面量），并自动重新执行图表与样本查询

  @ANALYTICS-S24 @auto:xunit
  Scenario: 生成的 SQL 只读高亮展示与复制
    When 展开图表探索页「生成的 SQL」折叠区
    Then SQL 经只读 CodeMirror 高亮渲染并保留 $start/$end 参数占位，复制按钮写入剪贴板成功

  @ANALYTICS-S28 @manual
  Scenario: 操作菜单先预览再复制查询
    Given 图表已有运行结果
    When 从数据源或运行区操作菜单选择预览查询
    Then 模态窗只读高亮展示查询与绑定参数，只有点击复制才写入剪贴板

  @ANALYTICS-S30 @auto:xunit
  Scenario: 页签切换渲染跟随激活页签
    Given 已在共享查询页签（时序图/表格/数值/柱状）运行出结果
    When 切换到另一共享查询页签
    Then 立即按新页签类型重查并渲染，上一页签的结果与错误不残留进新页签

  @ANALYTICS-S26 @auto:xunit
  Scenario: 图表支持四种真实数据来源
    Given 用户打开图表探索页
    When 选择日志、指标、客户端会话或链路并运行
    Then 图表与匹配样本使用所选来源，客户端会话按项目和会话标识去重

  @ANALYTICS-S27 @auto:xunit
  Scenario: 图表缩放同步查询时间
    Given 已运行的时序图
    When 缩小图表时间范围
    Then 时间选择器、图表和样本使用同一缩小区间

  @ANALYTICS-S40 @auto:xunit
  Scenario: 切换表格并按分组展示多个指标
    Given 图表探索已有时序查询结果
    When 用户切换表格并按多个维度查询多个指标或公式
    Then 展示不含时间桶的聚合表格，各指标按完整分组对齐且缺失值保持为空

  @ANALYTICS-S41 @auto:xunit
  Scenario: 表格筛选排序与展示
    Given 用户配置表格的聚合后条件和结果排序
    When 运行并调整表头排序、数字格式或下载结果
    Then 查询遵守配置且表格支持已返回结果排序和导出，内部计算列不展示

  @ANALYTICS-S42 @manual
  Scenario: 表格下钻与保存
    Given 用户运行带分组的表格
    When 点击分组行或保存至仪表盘
    Then 下钻沿用执行时来源和时间且保存后的指标列与展示配置一致

  @ANALYTICS-S25 @auto:xunit
  Scenario: 样本查询失败不掩盖图表结果
    Given 事件样本查询返回错误而图表查询成功
    When 展开「匹配事件样本」折叠区
    Then 样本区显式展示错误信息，图表仍正常渲染

  @ANALYTICS-S63 @manual
  Scenario: 事件搜索四种来源支持属性语法
    Given 用户选择日志、链路、指标或客户端会话来源
    When 切换属性语法并输入来源适用的字段条件或裸词后运行
    Then 展示来源说明与字段建议，条件值安全绑定且非法字段阻止查询，会话仍按项目和会话去重

  @ANALYTICS-S64 @manual
  Scenario: 属性搜索续页与保存恢复保持执行条件
    Given 用户已运行指标或客户端会话的属性搜索
    When 修改未运行草稿并续页，或保存后重新编辑
    Then 续页沿用已执行条件与时间，重新编辑恢复原始语法和条件且保存不携带临时分页位置

  @ANALYTICS-S60 @manual
  Scenario: 事件搜索使用独立配置并保留编辑状态
    Given 用户打开图表探索的事件搜索页签
    When 选择显示列、查询语言、条件和时间后运行，再切换页签返回
    Then 显示紧凑事件表且保留搜索配置，不展示聚合序列或重复样本区

  @ANALYTICS-S61 @manual
  Scenario: 事件搜索续页绑定执行条件并支持失败重试
    Given 事件搜索已展示首批结果
    When 编辑条件但未运行并加载下一批，或续页失败后重试
    Then 续页沿用已执行条件和时间，失败保留已有行且重试相同位置

  @ANALYTICS-S62 @manual
  Scenario: 事件搜索详情过滤及保存
    Given 用户运行日志或链路事件搜索
    When 展开事件、打开详情或按字段值过滤，并保存至仪表盘
    Then 详情沿用执行时间，过滤值安全绑定且保存不携带临时续页位置

  @ANALYTICS-S170 @manual
  Scenario: 模式标签独立查询并展示模板
    Given 用户进入图表探索的模式标签
    When 设置模式表达式、条件和时间范围并运行
    Then 展示趋势、数量、级别和模板且隐藏聚合序列控件

  @ANALYTICS-S171 @auto:xunit
  Scenario: 模式数量与趋势覆盖完整匹配窗口
    Given 时间窗内有动态编号不同的同类日志和不同级别的日志
    When 运行模式分析
    Then 同类日志归并为模板，按级别分别统计且趋势合计等于数量

  @ANALYTICS-S172 @manual
  Scenario: 模式列表展示与代表事件
    Given 模式查询已有结果
    When 切换换行、复制模板、导出或查看详情
    Then 操作基于当前结果且详情明确为代表事件

  @ANALYTICS-S80 @auto:xunit
  Scenario: 热力图展示完整时间窗的数值分布
    Given 时间窗内存在不同耗时的链路事件
    When 用户打开热力图并运行
    Then 横轴保留完整时间范围，纵轴表示耗时区间，颜色表示各区间事件数

  @ANALYTICS-S201 @auto:xunit
  Scenario: 长尾耗时分布保留零值与完整计数
    Given 链路耗时跨越多个数量级且含零耗时
    When 使用对数分桶查看热力图
    Then 正常请求与慢请求均可辨认，零值保留且所有格子计数合计等于有效事件总数

  @ANALYTICS-S202 @manual
  Scenario: 从热力格下钻并缩放时间
    Given 热力图已执行且用户修改了尚未运行的草稿
    When 点击热力格查看样本或框选时间并恢复范围
    Then 下钻仅使用已执行条件和选中区间，恢复后回到上一范围且不会混入草稿

  @ANALYTICS-S203 @manual
  Scenario: 热力图显示设置与兼容
    Given 用户调整热力图分桶尺度、精细度与计数色阶
    When 运行并分享链接或保存后重开
    Then 设置保持一致且旧配置沿用线性含义，指标负值不会被静默丢弃

  @ANALYTICS-S81 @manual
  Scenario: 热力图独立配置并保存
    Given 用户已运行耗时热力图
    When 切换页签返回或将结果保存至仪表盘
    Then 编辑配置保持且仪表盘沿用已执行分桶配置，不改变旧类别热力图含义

  @ANALYTICS-S43 @auto:xunit
  Scenario: 全时间窗聚合后选取排行榜
    Given 多个端点在所选时间范围内产生不同耗时的事件
    When 按端点计算请求数和耗时分位数并按分位数排序取前若干组
    Then 排行覆盖完整时间窗且窗口外事件不参与计算

  @ANALYTICS-S44 @manual
  Scenario: 表格独立配置与显示设置
    Given 用户配置表格的分组、多个聚合列及排序
    When 切换页签返回并调整斑马纹和表头吸顶
    Then 表格配置保留且滚动展示遵守设置，不影响时序图

  @ANALYTICS-S45 @auto:xunit
  Scenario: 表格默认展示来源对应的分组排行
    Given 所选来源在最近一天有观测数据
    When 用户进入表格页签或切换数据源
    Then 自动按来源默认分组查询前十组且不会显示上一来源的结果

  @ANALYTICS-S46 @auto:xunit
  Scenario: 借助提示配置筛选与聚合
    Given 用户不熟悉字段名称和查询语法
    When 选择分组和聚合字段并从真实值提示配置条件
    Then 字段类型与聚合匹配且条件值安全绑定，提示失败时仍可输入自定义值

  @ANALYTICS-S47 @manual
  Scenario: 表格空数据与未执行条件说明
    Given 表格已执行查询
    When 用户修改条件尚未运行或当前范围没有匹配事件
    Then 明确区分草稿与执行结果并提供恢复默认排行和扩大时间范围的入口

  @ANALYTICS-S189 @manual
  Scenario: 分享图表探索的当前搜索条件
    Given 用户在图表探索配置查询条件和时间范围
    When 切换页签或修改条件、排序并运行
    Then 地址自动保留当前页签及查询条件，重新打开后恢复条件并查询

  @ANALYTICS-S190 @manual
  Scenario: 图表链接导航与仪表盘编辑隔离
    Given 用户打开已有图表探索链接
    When 导航到另一探索链接并返回，或进入仪表盘图表编辑
    Then 探索条件随链接恢复，仪表盘编辑不改变地址中的查询条件

  @ANALYTICS-S187 @manual
  Scenario: 图表各页签提供来源适配的属性语法说明
    Given 用户在支持属性语法的图表页签编辑条件
    When 查看不同数据源和序列的使用说明并展开或收起，再切换查询语法
    Then 说明展示当前序列草稿与来源适用示例，操作不改条件也不运行查询，查询语法下不展示说明

  @ANALYTICS-S184 @manual
  Scenario: 数值图表选择和浏览真实指标
    Given 用户在数值图表选择指标数据源
    When 搜索指标或在树形与列表浏览器中查看类型和属性
    Then 只有确认使用才回填浏览器草稿且取消不改变序列

  @ANALYTICS-S185 @auto:xunit
  Scenario: 指标序列独立聚合并安全计算差值
    Given 多条序列选择不同指标及属性条件
    When 查询所选窗口的主数值和背景趋势或保存后重开
    Then 指标与属性条件保持独立且参数安全绑定，差值不跨标签组合计算

  @ANALYTICS-S186 @auto:xunit
  Scenario: 链路与会话遵守各自的数值查询口径
    Given 用户选择链路或客户端会话数据源
    When 配置字段表达式、分位数或去重计数
    Then 链路区分片段与链路计数，会话按项目与会话去重且无效文本数值聚合在运行前被拒绝
