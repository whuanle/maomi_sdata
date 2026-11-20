# dashboard — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 仪表盘

  @DASH-S112 @auto:xunit
  Scenario: 整盘导出为 JSON 文件
    Given 成员查看仪表盘列表或详情
    When 使用导出功能下载仪表盘 JSON
    Then 文件包含名称、描述与全部面板配置且只读成员同样可用

  @DASH-S113 @auto:xunit
  Scenario: 导入 JSON 快速创建仪表盘
    Given 成员在列表页使用导入功能
    When 选择或粘贴整盘 JSON 并确认
    Then 名称与描述按文件预填可修改且保存后列表出现新仪表盘

  @DASH-S114 @manual
  Scenario: 面板导出为 JSON 文件
    Given 成员在画布查看组件操作
    When 导出单个普通组件或分组
    Then 文件包含组件完整配置且分组包含全部子组件

  @DASH-S115 @manual
  Scenario: 面板导入追加到画布
    Given 成员在编辑态使用导入面板
    When 选择或粘贴面板 JSON 并确认
    Then 组件以新标识追加到画布底部且整盘保存后生效

  @DASH-S116 @auto:xunit
  Scenario: 导入校验失败不落库
    Given 成员粘贴非法 JSON 或不受支持的格式
    When 确认导入
    Then 提示解析失败且不创建仪表盘或不修改画布

  @DASH-S111 @auto:xunit
  Scenario: 创建入口按分组和组件方式排列
    Given 成员进入仪表盘编辑态
    When 查看创建操作工具栏
    Then 添加分组、自定义组件和添加组件依次排列

  @DASH-S108 @auto:xunit
  Scenario: 面板编辑和预览位置通过链接恢复
    Given 成员打开普通组件、分组或组内组件的编辑或预览链接
    When 刷新页面并重新加载已保存配置
    Then 仍进入对应组件与界面且预览不提供只读成员的写操作

  @DASH-S109 @auto:xunit
  Scenario: 面板界面位置恢复不泄露查询草稿
    Given 成员切换图表页签、分组标签或设计器显示设置
    When 地址更新并重新打开
    Then 对应界面位置恢复且地址不包含查询与参数，离开未应用的修改需要确认

  @DASH-S110 @auto:xunit
  Scenario: 分组编辑成员选择不会触发控件渲染错误
    Given 分组编辑器包含带名称的成员选择框
    When 打开编辑器并选择成员
    Then 名称与选择状态正常展示且不会将文本作为原生输入控件的子内容

  @DASH-S103 @manual
  Scenario: 重叠拖曳稳定排序
    Given 画布包含不同尺寸的多个组件和主动留白
    When 成员连续将组件拖到其它组件前后并调整大小
    Then 拖动中邻居立即预览让位与落位框，相同落点不重复计算，拖动项无延迟动画，来回移动不累计挤远且松手后布局保持

  @DASH-S104 @manual
  Scenario: 分组标签切换与成员管理
    Given 成员仅填写名称创建空分组
    When 将组件拖入中央或编辑分组手动添加并排序，保存后重新打开
    Then 空组和成员顺序保留且可切换标签，中央高亮收纳、边缘仍排序，分组不可嵌套

  @DASH-S105 @auto:xunit
  Scenario: 分组结构安全与兼容
    Given 仪表盘包含分组与旧格式组件
    When 校验配置并读取组件数据
    Then 旧组件保持兼容且仅叶子组件取数，重复标识、嵌套、无效默认标签及超量配置被拒绝

  @DASH-S106 @manual
  Scenario: 分组解散与只读展示
    Given 分组包含多个组件
    When 成员移出组件或解散分组并使用只读模式查看
    Then 组件不会丢失且只读成员只能切换标签，删除分组及成员需要再次确认

  @DASH-S101 @auto:xunit
  Scenario: 可视化选择数据并组合计算
    Given 成员在自定义组件中选择数据来源与时间范围
    When 使用智能提示筛选数据并选择聚合、分组和比率计算
    Then 无需编写查询即可预览多条数据且原始配置和安全绑定的筛选值保留

  @DASH-S102 @manual
  Scenario: 紧凑提示和高级查询兼容
    Given 成员打开自定义组件设计页
    When 查看属性提示并在可视化与高级模式之间切换
    Then 桌面提示控件排列在一行且旧查询不被自动反向解析或静默覆盖

  @DASH-S98 @auto:xunit
  Scenario: 独立自定义组件保留仪表盘草稿
    Given 成员已修改仪表盘但尚未保存
    When 进入独立设计页运行自定义组件并应用
    Then 原有修改和组件一同保留在草稿中且只有整盘保存才持久化

  @DASH-S99 @auto:xunit
  Scenario: 多查询表达式安全计算
    Given 组件包含独立绑定参数的多个查询与前序结果表达式
    When 运行计算
    Then 按完整时间与分组匹配结果且缺失值和除零为空值，重复键被拒绝

  @DASH-S100 @manual
  Scenario: 自定义组件设计与展示一致
    Given 成员已配置查询结果字段与展示格式
    When 应用后整盘保存并重新打开组件
    Then 查询、表达式、字段映射及展示设置保持且修改查询后须重新运行才能应用

  @DASH-S97 @manual
  Scenario: 新建图表直接应用探索结果
    Given 成员在仪表盘打开添加组件工作区
    When 运行图表并应用到画布
    Then 底部应用按钮直接保留已执行查询、参数和展示配置，无需重复确认或填写查询，未运行或切换无结果页签时不可应用

  @DASH-S95 @manual
  Scenario: 展示与图表编辑定义相互独立
    Given 仪表盘已保存包含公式与格式设置的图表
    When 面板不携带编辑恢复信息并重新打开
    Then 仍按已保存查询展示结果、公式显隐与格式且布局不变

  @DASH-S96 @manual
  Scenario: 旧面板连续编辑保持兼容
    Given 已有旧图表缺少完整编辑定义
    When 连续两次编辑并保存后重新打开
    Then 保留兼容查询入口、原展示语义与布局且不猜测原始构建条件

  @DASH-S94 @manual
  Scenario: 紧凑头部在不同模式与屏幕下保持可用
    Given 用户打开一个名称较长的仪表盘
    When 在亮暗主题和窄屏中切换查看、编辑与展示模式
    Then 编辑态操作紧跟名称输入框靠左排列，查看态编辑面板紧接运行按钮，时间范围、自动刷新和运行紧接展示按钮在同一工具栏内，窄屏可换行且无横向溢出，不展示描述输入框

  @DASH-S92 @manual
  Scenario: 模式与事件搜索面板保留结果语义
    Given 用户将模式或事件搜索保存为仪表盘面板
    When 重新打开仪表盘查看结果
    Then 模式展示趋势与模板，事件结果可分页浏览且内部辅助列不展示

  @DASH-E1 @manual
  Scenario: 共享图表编辑器往返编辑
    Given 成员在图表探索器运行含多序列和公式的图表
    When 保存到仪表盘并重新打开面板编辑
    Then 恢复原始筛选、分组、粒度、公式和显示设置

  @DASH-E2 @manual
  Scenario: 面板编辑只应用到本地草稿
    Given 成员在仪表盘编辑态打开图表工作区
    When 运行预览并应用图表
    Then 仅更新画布草稿且原面板标识和布局不变

  @DASH-E3 @manual
  Scenario: 旧图表配置安全降级
    Given 已有面板只保留执行查询而缺少完整编辑定义
    When 重新编辑该面板
    Then 明确使用兼容查询入口且不猜测或丢弃原配置

  @DASH-S50 @manual
  Scenario: 保存类别柱状图的执行配置
    Given 用户已运行类别柱状图并配置数值格式
    When 修改未运行草稿并保存到仪表盘后重新打开
    Then 保留已执行的分类排序与数量、绑定参数及数值格式，不启用时间分桶且原布局不变

  @DASH-PIE-SOURCES @manual
  Scenario: 四来源饼图保存已执行配置
    Given 用户已运行日志、链路、指标或客户端会话饼图
    When 修改尚未运行的分类并应用到画布后重新编辑
    Then 恢复已执行的来源、指标与分类且图例和原面板布局不变

  @DASH-S90 @manual
  Scenario: 保存饼图展示配置
    Given 用户已运行饼图并设置分类上限与数值格式
    When 保存到仪表盘并重新打开
    Then 保留查询参数和展示设置且已有面板布局不变

  @DASH-S70 @manual
  Scenario: 保存数值显示配置
    Given 用户已运行带属性或查询条件的数值图表并配置格式与背景趋势
    When 保存至仪表盘并重新打开
    Then 保留序列格式覆盖、规则顺序和独立趋势颜色，刷新同一时间范围仍更新趋势且旧面板正常展示

  @DASH-M1 @auto:xunit
  Scenario: Markdown 静态面板保存与安全预览
    Given 成员在 Markdown 页签输入标题、列表和表格
    When 查看预览并保存至仪表盘
    Then 正文完整保留且不执行数据查询或嵌入的 HTML

  @DASH-M2 @manual
  Scenario: Markdown 面板再次编辑与展示
    Given 仪表盘包含已保存的 Markdown 面板
    When 编辑正文并保存后重新打开仪表盘展示模式
    Then 显示更新后的正文且亮暗主题下均可阅读

  @DASH-F1 @manual
  Scenario: 保存公式图表并保持输入显示偏好
    Given 图表已有公式计算结果且已隐藏输入序列
    When 将图表保存到仪表盘并重新打开
    Then 公式定义和参数保留，面板仅显示公式结果且旧面板布局不变

  @DASH-S4 @auto:xunit
  Scenario: 图表探索结果追加到仪表盘
    Given 可写成员已经运行图表并选择目标仪表盘
    When 确认保存图表
    Then 保留已有面板和布局，追加图表并保留过滤值与展示设置

  @DASH-S5 @auto:xunit
  Scenario: 保存后的图表按仪表盘时间查询
    Given 已保存的自动粒度图表包含按值过滤
    When 修改仪表盘时间范围并运行
    Then 使用新的时间范围计算粒度且过滤值仍安全绑定

  @DASH-S1 @manual
  Scenario: 保存仪表盘并拉取面板数据
    Given 一个包含 stat 与 line 面板的仪表盘
    When 保存后请求 /api/dashboards/{id}/data
    Then 各面板返回 columns/rows 且互不影响（单面板失败含 error 字段）

  @DASH-S2 @manual
  Scenario: 非法配置被拒绝
    When 保存 configJson 不是合法 JSON
    Then 返回 400

  # @DASH-SC1/SC2/SC3 已于 2026-11-18 随 Screens 下线退役，场景 ID 不复用。

  @DASH-R1 @auto:xunit
  Scenario: 仅提供普通仪表盘
    Given 平台保留普通仪表盘与历史大屏配置
    When 成员浏览仪表盘列表或尝试新建大屏
    Then 列表及总数只包含普通仪表盘且新建大屏被拒绝

  @DASH-R2 @auto:xunit
  Scenario: 历史大屏保持原样且不可操作
    Given 历史大屏配置仍保存在元数据库
    When 成员尝试读取、执行、修改、删除或转换该配置
    Then 操作被拒绝且历史记录和配置保持原样

  @DASH-P1 @auto:xunit
  Scenario: 面板按 query.kind 分派执行
    Given version=1 配置含 metric/dsl/promql/sql 四种面板
    When 请求 /api/dashboards/{id}/data
    Then metric/dsl/promql 返回 series（按标签拆分、bucket 聚合、rate 速率），sql 返回 columns/rows，旧协议面板兼容执行

  @DASH-P2 @auto:xunit
  Scenario: 面板查询预览
    When POST /api/dashboards/preview-panel 提交未保存面板
    Then 返回 PanelData；非法 kind/缺指标名/无 bucket 无聚合等在 PreviewPanelCommand 校验被拒（400）

  @DASH-P3 @auto:xunit
  Scenario: PromQL rate 区间回看语义
    Given 计数器样本 (100@-300s, 200@-150s, 400@-1s)，rate(x[5m]) step=60s
    Then 每评估点按 5m 窗口回看（跨 step 桶），含数据桶 rate ≈ [0, 0, 0.33, 0.33, 1.0, 0.67]

  @DASH-P4 @manual
  Scenario: 面板编辑器实时预览与保存
    Given 编辑态打开添加面板抽屉
    When 切换数据来源（metric/dsl/promql/sql）、配置查询
    Then 预览防抖刷新（后端校验错误内联显示），保存面板后写入 configJson 并刷新渲染

  @DASH-S3 @manual
  Scenario: 时间序列统一展示
    Given 面板包含多条序列和缺失采样点
    When 查看折线、面积或柱状图
    Then 图例可区分序列，坐标轴和提示值使用所选单位，缺失点不填零且不自动堆叠

  @DASH-W1 @manual
  Scenario: 新建 Dashboard 进入空画布
    Given 列表点击新建并只填名称与描述
    When 保存成功
    Then 跳转 /dashboards/:id 独立画布并处于编辑态，无需手写配置 JSON

  @DASH-W2 @manual
  Scenario: 组件拖拽布局与草稿保存
    Given 编辑态添加真实数据组件（metric/dsl/promql/sql）
    When 拖动手柄移动、拖右下角调整尺寸后点保存
    Then 布局坐标与查询写入 configJson（version=2），刷新后位置尺寸保持

  @DASH-W3 @manual
  Scenario: 仪表盘展示模式复用同一布局
    Given 已保存的 Dashboard
    When 打开 /dashboards/:id/present
    Then 按相同坐标全屏渲染且无编辑控件；未保存修改时提示先保存

  @DASH-S93 @manual
  Scenario: 分布热力图保留尺度与全窗口统计
    Given 用户已运行对数耗时热力图并保存至仪表盘
    When 重新打开面板并改变时间范围
    Then 数值尺度与计数色阶保持且全窗口重新分桶，旧配置仍保留线性含义

  @DASH-S91 @manual
  Scenario: 保存聚合表格的已执行配置
    Given 聚合表格已按指定分组和排行榜数量运行
    When 修改草稿但未运行并保存表格到仪表盘后重新打开
    Then 面板保留已执行查询、排行榜数量、指标身份及展示设置

  @DASH-S107 @auto:xunit
  Scenario: MQTT 设备遥测面板保存与恢复
    Given 成员选择设备遥测数据源并配置设备筛选与数值聚合
    When 生成查询并保存面板后重新加载定义
    Then 数据源、绑定条件和分组保留且沿用既有只读查询通道
