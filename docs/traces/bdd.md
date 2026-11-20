# traces — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[store](../store/bdd.md) · 证据：[前端测试](../../ui/src/pages/search/__tests__/TraceDetailDrawer.test.tsx)

Feature: 链路查询与分析

  @TR-S15 @manual
  Scenario: 服务地图利用窗口剩余高度
    Given 用户打开有数据的服务地图
    When 调整窗口高度或展开服务详情
    Then 地图填满工具栏下方剩余空间且详情独立滚动，小窗口保留可操作的画布高度

  @TR-S14 @manual
  Scenario: 服务地图工具栏按宽度紧凑排列
    Given 用户打开服务地图
    When 使用宽屏或缩小窗口
    Then 服务和语法选择保持紧凑且搜索框利用剩余空间，窄屏按功能分组换行

  @TR-S10 @manual
  Scenario: 按时间和条件梳理服务依赖
    Given 多个服务在指定时段内参与链路
    When 选择服务并使用查询条件分析服务地图
    Then 展示命中链路中的上下游关系而非仅保留命中片段

  @TR-S11 @auto:xunit
  Scenario: 整条链路采样且不混淆片段身份
    Given 不同链路存在相同片段标识和重复采集片段
    When 按链路比例采样并分析服务关系
    Then 父子关系不跨链路串联且重复片段不放大统计

  @TR-S12 @manual
  Scenario: 从依赖关系查看真实顺序
    Given 地图展示某时段内的聚合关系
    When 缩小时间范围并查看某条依赖的链路样例
    Then 可查看真实调用顺序与并发且不会将聚合路径当作单条链路

  @TR-S13 @manual
  Scenario: 不完整关系与展示限制可识别
    Given 存在窗口外父片段或关系数量超出展示范围
    When 查看采样后的服务地图
    Then 显示采样口径、缺父与裁剪提示且可调整条件重新分析

  @TRACES-SEARCH1 @auto:xunit
  Scenario: 按业务属性筛选并聚合链路
    When 使用服务、耗时与业务属性筛选链路并汇总耗时分布
    Then 返回符合条件的统计结果

  @TRACES-SEARCH2 @auto:xunit
  Scenario: 查询跨天链路
    When 查询时间范围跨越多个日期
    Then 返回范围内各日期的链路数据

  @TRACES-SEARCH3 @auto:xunit
  Scenario: 拒绝不支持的查询条件
    When 使用不支持的字段或操作筛选链路
    Then 显示可理解的错误提示

  @TRACES-UI-DEEPLINK @manual
  Scenario: 从日志定位链路
    Given 日志包含链路标识
    When 查看该链路
    Then 搜索切换至链路并按标识筛选当前时间范围内的记录

  @TR-S1 @manual
  Scenario: 使用链路专属过滤器
    Given 当前查看链路搜索
    When 按服务、名称、类型、状态、属性、根节点或耗时筛选
    Then 筛选条件可清除并随地址恢复且不显示日志降噪控件

  @TR-S2 @manual
  Scenario: 就地展开完整片段
    Given 结果表仅显示部分列
    When 展开一条链路片段
    Then 可在概览与列值间切换并搜索完整属性而不打开侧栏

  @TR-S3 @manual
  Scenario: 时间轴与片段属性联动
    Given 已打开链路宽侧栏
    When 在调用树或时间轴选择另一片段
    Then 右侧切换到该片段的属性并保持当前链路与查询时间窗

  @TR-S4 @manual
  Scenario: 有限数据与加载失败可辨识
    Given 链路超出查询窗口或存在缺失父节点
    When 查看链路详情
    Then 显示窗口与不完整提示且加载失败可以重试

  @TR-S5 @auto:xunit
  Scenario: 短片段和事件保持精度
    When 采集不足一毫秒的片段及带属性的事件
    Then 保留微秒耗时、成功状态与事件属性

  @TR-S6 @manual
  Scenario: 缩放时间轴并查看关联记录
    Given 链路包含短片段、日志与事件
    When 缩放概览选区并选择日志或事件
    Then 主时间轴同步缩放且显示正确耗时与所选记录详情

  @TR-S9 @manual
  Scenario: 从调用树和时间条末端识别同一片段
    Given 链路包含长名称和极短耗时的片段
    When 对照调用树与时间条末端并缩放时间轴
    Then 两端均可识别片段且末端优先展示耗时，长名称省略且不超出画布
    When 点击末端标签
    Then 选中对应片段并更新详情，裁剪位置不被误认为真实结束

  @TR-S7 @manual
  Scenario: 从片段查看周边链路和日志
    Given 已选中包含服务与主机属性的片段
    When 查看上下文并调整时间窗、结果类型与匹配属性
    Then 按条件分批展示附近记录且查看详情不会移动上下文锚点

  @TR-S8 @manual
  Scenario: 大链路与不完整关联可辨识
    Given 链路包含大量片段及无法关联片段的日志
    When 滚动、折叠并筛选时间轴
    Then 可继续浏览且未关联日志、折叠数量与截断提示不会静默消失
