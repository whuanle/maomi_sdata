# rum — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 真实用户监控（RUM）接入

  @RUM-S1 @auto:vitest
  Scenario: 会话工作台检索与返回
    Given 已选择项目与时间范围
    When 执行搜索并打开会话后返回
    Then 查询条件保留且列表显示匹配会话

  @RUM-S2 @auto:xunit
  Scenario: 会话分页与项目隔离
    Given 不同项目存在同名会话且部分事件在时间范围外
    When 筛选并分页浏览会话及其回放
    Then 只显示当前项目和时间范围内的数据且分页不重复

  @RUM-S3 @auto:vitest
  Scenario: 采集失败恢复与录制尾段
    Given 采集通道暂时不可用
    When 通道恢复或录制停止
    Then 失败数据重发且录制尾段不丢失

  @RUM-S4 @manual
  Scenario: 浏览器采集到回放
    Given 测试站点已接入探针
    When 点击按钮改变页面后打开对应会话
    Then 会话包含点击事件且播放器可重现页面变化

  @RUM-S5 @manual
  Scenario: 接入权限与采样配置
    Given 管理员已创建项目并配置采样率
    When 只读用户查看项目并尝试创建项目
    Then 查看成功但创建被拒绝且探针获取已配置采样率

  @RUM-S6 @auto:vitest
  Scenario: 会话页面快速创建项目
    Given 管理员打开会话页面
    When 点击工具栏的创建项目并提交有效配置
    Then 新项目自动选中且项目列表刷新，无需先打开接入配置

  @RUM-S7 @auto:vitest
  Scenario: 非管理员查看接入帮助
    Given 写入或只读用户打开会话页面
    When 查看工具栏和接入配置
    Then 可以查看接入代码但看不到创建项目入口

  @RUM-S8 @manual
  Scenario: 会话工具栏紧凑排列
    Given 用户打开会话页面
    When 在不同窗口宽度下查看查询工具栏
    Then 项目选择器靠左且保持紧凑宽度，错误、回放和排序筛选横向并排，窄屏才换行

  @RUM-S9 @auto:xunit
  Scenario: 严格限制项目采集来源
    Given 项目配置了完整允许站点
    When 错误来源、未知项目或删除项目尝试上报
    Then 请求被拒绝且不写入或广播数据

  @RUM-S10 @auto:xunit
  Scenario: 跨域与实时通道共同限制来源
    Given 采集站点分为允许与未允许来源
    When 站点发起预检、上报或实时连接
    Then 仅允许站点可采集且管理端认证同源订阅仍可用

  @RUM-S11 @auto:vitest
  Scenario: 管理员编辑允许站点
    Given 管理员打开已有项目的接入配置
    When 保存合法允许站点或提交非法站点
    Then 合法配置保存并保留原采样率，非法配置被提示且不提交

  @RUM-R4 @manual
  Scenario: 图表样本打开客户端会话回放
    Given 客户端会话已有回放分片
    When 在图表探索页打开该会话样本
    Then 展示会话详情并按图表查询区间加载回放

  @RUM-B1 @auto:xunit
  Scenario: gzip beacon 摄取按类型分派入库
    When POST /rum/v1/beacon（gzip JSON，C3 形状）
    Then pageview → rum_session_、vital/error/click/custom → rum_event_、replay-chunk → rum_replay_ 天表各有行

  @RUM-B2 @auto:xunit
  Scenario: 键无效拒绝
    When beacon 缺 projectKey 或 JSON 非法
    Then 返回 401

  @RUM-B3 @auto:xunit
  Scenario: 缓冲满背压
    Given 接入缓冲满
    When 提交 beacon
    Then 返回 429

  @RUM-B4 @auto:xunit
  Scenario: 未授权采集来源不能获取配置
    Given 项目未创建或请求来源不在允许站点中
    When 探针请求采样配置
    Then 请求被拒绝且不返回默认全采样配置

  @RUM-B5 @manual
  Scenario: 探针脚本托管
    When GET /rum/probe.js
    Then 返回可用的浏览器探针脚本，缺少构建产物时明确提示不可用

  @RUM-P1 @auto:xunit
  Scenario: RUM 项目 CRUD
    When admin 新增项目（rum_demo，采样率 0.5/0.2）→ 查询 → 修改采样率 → 删除
    Then ProjectKey 不可变、唯一（重复 409）、软删除生效、列表分页正确

  @RUM-P2 @auto:xunit
  Scenario: 只读用户写接口 403
    Given Save/Delete 接口标注 [Authorize(Roles=admin)]
    When write/readonly 用户调用
    Then HTTP 层 403（角色不在授权集；单测反射断言角色配置）

  @RUM-P3 @manual
  Scenario: config 按项目采样下发
    When GET /rum/v1/config?projectKey=rum_demo（已建项目）
    Then 允许站点获取该项目保存的采样率，其他来源被拒绝

  @RUM-Q1 @auto:xunit
  Scenario: 性能汇总 PV/UV 与 Web Vitals p75
    Given beacon 写入 2 个会话各 1 次 pageview、LCP 3 个值（1000/2000/3000）与 CLS 0.1
    When GET /rum/performance?projectKey=rum_demo（当日区间）
    Then PV=2、UV=2（distinct 会话近似）、LCP p75=2500（percentile_cont 插值）、CLS p75=0.1

  @RUM-Q2 @auto:xunit
  Scenario: JS 错误 Top 按次数降序
    Given error 事件 TypeError×2 与 ReferenceError×1
    When GET /rum/errors?projectKey=rum_demo&topN=10
    Then 首项 TypeError 计数 2、次项 ReferenceError 计数 1；其他项目查询为空（项目隔离）

  @RUM-Q3 @auto:xunit
  Scenario: 会话列表按最近活跃降序
    Given 两个会话相隔 1 分钟活跃且 pageview 携带 extra.ua/extra.domain
    When GET /rum/sessions?projectKey=rum_demo
    Then 列表按 last_seen 降序并带 UA/域名；空库区间查询短路返回空列表

  @RUM-R1 @auto:xunit
  Scenario: 会话回放分段读取（T614）
    Given 三段 replay-chunk（seq 乱序上报）经 beacon 摄取 rum_replay 天表
    When GET /api/rum/v1/replay/{sessionId}
    Then 分段按 seq 升序返回且内容与上报一致；其他会话查询为空

  @RUM-R2 @auto:xunit
  Scenario: SDK 录制→分段→上报数据链路
    Given 录制器工厂注入（rrweb record 兼容）且回放采样命中
    When 事件攒满 chunkEvents 切段：gzip+base64、seq 单调递增、经 beacon type=replay-chunk 上报
    Then 采样率 0 时不启动录制；未注入录制器时零行为；CompressionStream 缺失回退原始 base64

  @RUM-R3 @manual
  Scenario: rrweb-player 回放视图（e2e 待集成窗）
    Given ui /rum 页回放 Tab 选中会话
    When 分段解码（base64→gzip→JSON 拼接）后挂载 rrweb-player
    Then 浏览器内完整回放会话操作（播放器不可用时降级原始事件预览）

  @RUM-H1 @auto:xunit
  Scenario: 点击热力栅格聚合
    Given click 事件携带 extra 坐标（x/y/pageW/pageH）入库 rum_event.payload
    When GET /rum/heatmap?projectKey=rum_demo&gridCells=20
    Then 坐标按 20x20 归一化分桶，同桶点击合并计数（如 (990,55) 与 (985,60) 同落 (19,1) 桶 count=2）

  @RUM-H2 @auto:xunit
  Scenario: 站点地图页面树聚合
    Given 多页面存在 click 与 error 事件
    When GET /rum/sitemap?projectKey=rum_demo
    Then 按页面路径聚合 clicks/errors（click 降序），页面间互不串数

  @RUM-H3 @auto:vitest
  Scenario: 点击热力图呈现
    Given 当前项目存在点击栅格数据及零次点击噪点
    When 用户打开热力视图
    Then 横纵坐标按栅格类目展示且仅渲染有点击的单元格

  @RUM-L1 @manual
  Scenario: 实时点击通道（hub 直报→订阅推送，T616）
    Given 探针以 /hub/rum?projectKey= 建立 SignalR 连接，管理端经 query access_token 认证
    When 探针 SendClick 直报 → IRumLivePublisher 入 IIngestBuffer + 广播 view:{projectKey} 组；管理端 Subscribe 收 click 推送
    Then 未认证 Subscribe 抛 HubException；projectKey 缺失的 SendClick 拒绝；实时面板点击流/速率/迷你热力随推送更新

  @RUM-L2 @manual
  Scenario: beacon 批量通道点击同样实时广播（T616）
    Given POST /rum/v1/beacon 摄取 click 事件成功入缓冲
    When RumBeaconService.Dispatch 调 IRumLivePublisher.PublishClick
    Then beacon 批量上报的点击同样出现在实时面板（两通道共用发布入口，不重复入缓冲）

  @RUM-L3 @auto:xunit
  Scenario: 热力聚合文档绝对坐标优先/视口回退与页面过滤（T616）
    Given click extra 携带 pageX/pageY/docW/docH（新）或仅 x/y/pageW/pageH（存量）
    When AggregateHeatmapAsync 按 coalesce(pageX,x)/coalesce(docW,pageW) 归一化分桶，pagePath 参数绑定过滤
    Then 文档坐标 (500,1000)/(1000,2000)→(10,10) 桶；存量视口 (990,55)/(1000,1000)→(19,1) 桶；页面过滤后只含该页点击且 TotalClicks 口径一致

  @RUM-L4 @auto:xunit
  Scenario: 点击目标 TopN（T616）
    Given click extra 携带 selector（新）或 targetId（存量），部分点击无目标信息
    When GET /rum/heatmap 聚合 json_extract_string(selector/targetId)
    Then selector 优先、targetId 回退，无目标点击不计入；按次数降序返回 Top 10

  @RUM-L5 @auto:vitest
  Scenario: SDK SignalR 实时通道与断线回落（T616）
    Given init({ realtime: true })（连接工厂可注入）
    When 连接成功点击即发即传不进批量队列；连接失败/断线 sendClick 返回 false 回落批量队列
    Then 恢复后重新建连直报；hub 端 SendClick 返回 false（缓冲满）时同样回落

  @RUM-L6 @auto:vitest
  Scenario: SDK 上报携带 projectKey/sessionId（T612 拒收缺陷修复，T616）
    Given init({ projectKey }) 后 beacon 批量通道 flush
    When 上报体 JSON 序列化
    Then 携带 projectKey 与 sessionStorage 会话标识（同标签页稳定），服务端不再空 key 拒收

  @RUM-COLD1 @auto:xunit
  Scenario: 会话和关联数据整体转冷
    Given 会话、事件和回放分段位于已到期的历史日期
    When 自动归档到冷存储
    Then 原会话检索、事件列表与回放仍可读取，迟到事件可追加归档
