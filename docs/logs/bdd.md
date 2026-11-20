# logs — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 日志查询

  @LOGS-S1 @manual
  Scenario: 按服务与级别过滤日志
    Given 已接入 plc-gateway 的 ERROR 与 INFO 日志
    When 以 service=plc-gateway、severity=ERROR 查询
    Then 结果仅包含 ERROR 级别日志且按时间倒序

  @LOGS-S2 @manual
  Scenario: 关键字检索
    When 以关键字 "timeout" 检索
    Then 仅返回 body 含该关键字的日志

  @LOGS-S3 @manual
  Scenario: 服务名列表
    When 查询日志服务列表
    Then 返回已接入过的服务名集合

  @LOGS-SEARCH1 @auto:xunit
  Scenario: 统一查询 DSL 过滤与排序（POST /api/logs/search）
    When 提交 range/filters（service eq、severity prefix）与 sort/limit
    Then 返回列式结果（ts/service/severity/body/... 列），一切外部值参数绑定

  @LOGS-SEARCH2 @auto:xunit
  Scenario: JSON 标签过滤与时间分桶
    When 过滤 attributes.k8s.pod 并按 bucket.step=5m count 分桶
    Then json_extract_string 提取标签、time_bucket 输出 bucket 列并升序
    Then 查询跨度超过 MaxQueryDays 时返回 400

  @LOGS-SEARCH3 @auto:xunit
  Scenario: JSON 数值比较（统一搜索 attributes:>N 语法）
    When 过滤 attributes.status_code >= 400
    Then TRY_CAST(json_extract_string(…) AS DOUBLE) 数值比较（白名单语义见 STORE-DSL5）

  @LOGS-UI-JSONDISC1 @auto:xunit
  Scenario: JSON 属性发现（嵌套提取与字面点分 key 互不串查）
    Given attributes 列混存 {"http.status_code":"500"}（字面点分 key）与 {"http":{"status_code":500}}（嵌套对象）两形态日志
    When 按绑定路径 $."http.status_code" 与 $."http"."status_code" 分别提取
    Then 两查询各命中各自形态的行（单层 key 不做嵌套展开、嵌套路径不命中字面 key），提取路径全参数绑定
    When body 为 JSON 数组文档或普通文本
    Then 数组按显式索引提取，json_valid 防护使文本行安全返回 NULL 不报错

  @LOGS-UI-JSONDISC2 @auto:xunit
  Scenario: JSON 子属性逐层枚举与带引号别名投影
    When 对嵌套节点按绑定路径 json_extract 后 json_keys 枚举子属性（抽样口径同 facet）
    Then 返回节点全部子 key（根列/节点双重 json_valid 防护）
    When 以显示名作带引号列别名投影 json_extract_string 结果
    Then DuckDB 原样返回括号路径形态列名（如 attributes["http"]["status_code"]）

  @LOGS-UI-JSONDISC3 @manual
  Scenario: 搜索页 JSON 属性树浏览与勾选（SELECT/过滤器树）
    Given 搜索页已执行过一次搜索（发现树锚定当前搜索时间窗，抽样估计口径）
    When 打开 SELECT 显示列浮层展开 attributes/resource（日志域含日志体 JSON 组）
    Then 逐层懒加载子属性（服务端 json_keys 枚举，支持按键名搜索、Load more、失败重试）
    When 勾选子字段复选框
    Then 该字段以括号路径形态加入结果列，查询以绑定路径提取并以显示名作列别名，结果表按此列渲染
    When 点击过滤器树 key 行的「子属性」按钮逐层展开嵌套 key
    Then 嵌套 key 可继续展开值分布、加入存在性过滤（操作语义与顶层 key 一致）

  @LOGS-UI-SUGGEST1 @auto:xunit
  Scenario: WHERE 输入框智能建议（Lucene/SQL）
    Given 搜索页行 2 WHERE 输入框获得焦点
    When 输入裸词前缀（如 sev）
    Then 弹出候选列表（JSON 根 + 物理列，含字段类型提示），↑/↓ 高亮、Enter/Tab 采纳后词元替换为字段引用并补冒号，弹层打开时 Enter 不触发查询
    When 输入 field:（如 severity:ER）
    Then 建议该字段 top 值分布（服务端 facet，防抖），采纳值后补空格衔接下一子句且不触发查询
    When 输入 JSON 括号路径前缀（如 attributes["http"]["）
    Then 建议该节点子属性（服务端 json_keys 按绑定路径枚举）
    When 无弹层时按 Enter / IME 组合输入期间按 Enter
    Then 前者直接执行查询；后者不采纳不执行（输入法优先）

  @LOGS-UI-DRAWER1 @manual
  Scenario: 行点击打开日志详情抽屉（HyperDX 式头部与 Body）
    Given 搜索页结果表返回日志行（所选列不含 attributes/resource）
    When 点击结果行
    Then 右侧滑出详情抽屉，按 ts+service（+severity/trace_id/span_id 在场即并入）身份条件补查完整记录
    Then 头部元信息行展示 severity 着色、绝对时间（毫秒精度）+ 相对时间（"3m ago"）、服务与 Trace ID（可点击）
    Then 头部「查看链路」按钮（悬浮提示"一键跳转该日志关联的完整链路"）触发链路跳转
    Then Body 全文固定展示在页签区之上（含复制与 ServiceName chip），概览页签不再重复消息体

  @LOGS-UI-DRAWER2 @manual
  Scenario: 行首箭头就地展开与抽屉入口区分
    When 点击行首收缩箭头
    Then 结果行就地展开所选列全字段明细并可按值过滤，不打开抽屉
    When 点击行首箭头外的行区域
    Then 打开详情抽屉

  @LOGS-UI-DRAWER3 @manual
  Scenario: 概览/列值页签分区折叠与值行快捷动作
    When 查看概览页签
    Then 顶层属性/日志属性/资源属性分区可折叠展开，每分区带按键或值搜索框
    When 悬浮属性值行
    Then 出现「按此值过滤」「仅此值」「复制值」动作（日志属性键按 attributes.<key> 路径过滤）
    When 切换到列值页签
    Then 整条记录渲染为单棵 JSON 结构树（attributes/resource/events 解析为嵌套可折叠节点，不再逐字段铺开）
    Then 记录级搜索框按分支过滤（键名或叶子值命中即保留分支），并提供「复制整行 JSON」

  @LOGS-UI-DRAWER4 @manual
  Scenario: 链路日志页签（Open in search）
    Given 目标日志带 trace_id
    When 切换到链路日志页签
    Then 顶部「在搜索中打开」按钮以 trace_id 等值过滤回到主搜索
    Then 按 trace_id 在 ±1h 窗内查询并列出该链路全部日志（带表头），点击条目切换抽屉目标

  @LOGS-UI-DRAWER5 @manual
  Scenario: 服务地图页签（仅 trace_id 时展示）
    Given 目标日志带 trace_id
    When 切换到服务地图页签
    Then 懒加载 GET /api/traces/{traceId} 全部 span，按 span_id→service 映射聚合服务节点与调用边（同服务内部调用不成边）
    Then echarts 力导向拓扑：节点大小=span 数，可按延迟/错误率/吞吐量切换着色，悬浮展示调用次数/错误率/平均耗时

  @LOGS-UI-DRAWER6 @manual
  Scenario: 上下文页签（±时间窗 + Match on）
    When 切换到上下文页签
    Then ±时间窗 8 档（100ms/500ms/1s/5s/30s 默认/1m/5m/15m）切换即重查
    Then 匹配方式支持任意（仅时间窗）/同服务/属性点选；当前记录顶层列与 attributes/resource 键值以 chip 展示可点选加入匹配
    Then 属性匹配条件经白名单编译且值全参数绑定，按记录身份高亮，查看详情保持上下文锚点不变

  @LOGS-S7 @manual
  Scenario: 按主机和嵌套属性查看邻域
    Given 当前日志包含主机与嵌套业务属性
    When 选择同主机或自定义属性匹配并查看附近记录
    Then 条件按且组合且查看详情不会改变原查询窗口
    When 显式以选中记录重新定位上下文
    Then 时间窗围绕新记录重查并保留微秒锚点

  @LOGS-S6 @manual
  Scenario: 超长日志消息体自动收缩
    Given 日志消息体包含超长单行或大量换行
    When 打开日志详情
    Then 消息体默认限高并自动换行，不撑宽侧栏
    When 展开消息体
    Then 完整内容在限高区域内滚动，可再次收起且复制仍包含全文

  @LOGS-S4 @manual
  Scenario: 上下文分批加载至结束
    Given 时间窗内有超过一百条匹配日志
    When 打开上下文页签
    Then 首批仅展示一百条日志
    When 滚动到列表底部
    Then 每次追加最多一百条，全部加载后显示结束标识

  @LOGS-S5 @manual
  Scenario: 上下文续载失败与条件切换
    Given 已加载一批上下文日志
    When 后续加载失败
    Then 已有日志保留并提供重试
    When 切换时间窗或匹配条件
    Then 清空旧列表并重新加载首批，旧请求结果不混入

  @LOGS-UI-TRACEJUMP @manual
  Scenario: 日志 trace_id 点击跳转链路视图
    When 点击结果行或抽屉中的 trace_id
    Then 跳转 /search?source=traces&traceId=…（push 历史栈可回退），URL 参数落地为 trace_id 过滤器查询该链路全部 span
    Then 刷新页面后仍按该 traceId 过滤（深链可分享）
