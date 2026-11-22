# rum — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md)

## 结构

- `SData.Rum.Shared`：请求/响应模型（RumConfigResponse/RumBeaconRequest/RumEventPayload/RumLiveClickEvent）+ `IRumBeaconService`、`IRumLivePublisher`（实现含 IHubContext 依赖放 Api）
- `SData.Rum.Core`：`RumBeaconService`（gzip 解码 → JSON 解析 → 按 C3 type 分派 IIngestBuffer 三队列；click 入缓冲成功后经 IRumLivePublisher 实时广播）
- `SData.Rum.Api`：`RumBeaconController`（POST /rum/v1/beacon、GET /rum/v1/config、GET /rum/probe.js）+ `RumQueryController`（performance/errors/sessions/heatmap/sitemap/replay）+ `RumLiveHub`（/hub/rum，T616）+ `HubRumLivePublisher`（入缓冲 + 广播 view:{projectKey} 组）

## 数据

- `rum_event_/rum_session_/rum_replay_` 三天表族（TimeTables.CreateSql，T610 占 DDL）
- 项目实体 RumProjectEntity 随 T611 落地；此前 beacon 宽容模式（projectKey 非空即收）
- click extra/payload 键位（T616）：视口坐标 `x/y` + 文档绝对坐标 `pageX/pageY`（clientX+scroll）+ `docW/docH`（归一化分母）+ `selector`（tag#id.class 两级短链）+ 存量 `pageW/pageH/targetId`
- 热力聚合（T616）：`AggregateHeatmapAsync` 文档坐标优先 coalesce(pageX,x)/coalesce(docW,pageW) 归一化分桶，pagePath 参数绑定过滤，返回栅格+TotalClicks+TopTargets（json_extract_string 提取——json_extract 返回 JSON 逻辑类型，与 '' 字面量统一类型会隐式 CAST('' AS JSON) 报错，DuckDB 1.5 实测）
- 热力视图使用双类目轴与栅格索引，过滤零点击单元格；图表配置遵循 `EChartsOption`，提示回调校验数组数据后还原百分比坐标。

## 实时通道（T616）

- 端点：`MapHub<RumLiveHub>("/hub/rum")`（宿主 MainExtensions，AddSignalR 在 ConfigureSignalRModule）
- 探针：握手 query `?projectKey=`，`SendClick` → IRumLivePublisher（入 IIngestBuffer 走既有 DuckDB 批量落库链路 + 广播），返回 false 表示缓冲满（SDK 回落批量通道）
- 管理端：JWT 经 query `access_token`（ConfigureAuthorizaModule OnMessageReceived 仅对 /hub 前缀放行），`Subscribe(projectKey)` 校验已认证后加入 view:{projectKey} 组收 `click` 推送
- 来源保护：`RumOriginPolicy` 按项目 `DomainsJson` 精确校验完整 HTTP/HTTPS Origin，空列表/未知/删除项目拒绝采集；可选 `SData:Rum:CorsOrigins` 为额外全局上限。Origin 优先，仅缺失时回退 Referer，不信任事件 domain。
- CORS 仅作用于公开 RUM 端点与 Hub；预检按允许站点集合，实际请求按目标项目再校验。WebSocket 握手独立校验，SendClick 每次重新检查；管理端保留认证同源订阅。脚本公开，来源限制不是认证。

## 图表探索接入

- Chart Explorer 的 Sessions 来源读取 RUM 会话天表，窗口内按项目与会话标识去重；ts 是窗口内首次上报，不代表会话真实起始时间。
- 会话样本详情复用 ReplayView，传入图表执行的绝对区间；独立播放器不传范围时使用近 24 小时。Client Session 真实采集及回放已验收，图表入口完整浏览器验收状态见 [tdd](tdd.md)。

## Client Session 工作台（2026-11-18）

- 主入口 `/sessions`，旧 `/rum` 保留查询参数跳转；默认会话列表，顶部项目、包含匹配搜索、绝对时间、执行与刷新，性能/热力/实时/站点地图保留为辅助视图。
- 工具栏左对齐，项目选择器固定 208px 且不收缩，搜索框弹性占用剩余空间；时间与操作控件按可用宽度自然换行。会话列表的错误、回放与排序筛选各 176px，横向并排，仅空间不足时换行。
- 管理员在工具栏直接打开独立的创建项目表单，无需进入接入配置；创建成功后刷新项目列表并选中新项目。接入配置提供探针代码、隐私提示与管理员允许站点编辑，保存来源时保留项目名称和采样率；非管理员隐藏写操作。
- 新检索聚合三类天表，按项目和 `[from,to)` 限定后统计窗口内首末活动、浏览/错误/事件数及回放状态；服务端分页和白名单排序，所有外部值参数绑定。
- 回放读取必须提供项目标识；详情含事件时间线（最多 1000 条）、URL 深链、播放器尺寸调整与销毁、损坏分片告警。跨页面 seq 重置时按分片时间及事件时间恢复顺序。
- 项目列表前缀修正为单个 `/api`；探针接口显式注册，已建项目下发保存的采样率。未建项目不再允许采集，不将公开项目键视为身份凭证。
- `ui/vite.probe.config.ts` 生成真实 IIFE 探针；浏览器入口内置 rrweb，默认遮蔽输入，`sdata-private` 阻止敏感区域录制。构建缺失时端点返回 503，不返回假成功脚本。
- SDK 修复失败队列重发、实时背压回落、录制尾段；实时尺寸 JSON 键统一为 docW/docH，管理端重连后重新订阅。
- 内网 HyperDX 仅显示配置引导：已对照工具栏和引导布局，列表及详情基于 SData 数据能力实现，非未经观察的逐项克隆。

## 后续

- T612 探针 SDK、T613 查询面、T614 回放、T615 热力、T616 实时热力（SignalR）/坐标增强/页面过滤
- RUM 三表族 rum_session/rum_event/rum_replay 共用 Retention.Rum；RumStore 所有查询及分析 sessions 来源经台账解析冷热数据，项目与时间参数绑定不变。

## Go 迁移边界（2026-11-22）

- `go/internal/rum` 实现项目元数据、严格 Origin/Referer 校验、gzip/plain beacon 和全部普通查询；`go/cmd/sdata/rum_handlers.go` 提供独立 HTTP 适配器，管理写操作要求 admin。
- SQLite 仅保存 `rum_project`；初始化增量补充采样率和来源列，兼容已有 C# 项目，不删除旧 Go 占位时序表，也不将其历史数据冒充 DuckDB 数据。
- `go/internal/store/rum.go` 复用总行数/字节预算、后台批量 Appender、关闭排空和统计；按事件 UTC 日期写入与 C# 相同列顺序的三个天表族。请求线程不执行 DuckDB 写入。
- 查询全部绑定项目、时间、分页、路径和会话参数；窗口最多 30 天，事件最多 1000 条；会话搜索合并三族数据。保留普通 sessions 旧响应口径，完整摘要使用 sessions/search。
- 启动经 `initializeRum`（生产者启动前），路由绑定 `handleRum*`；共享表名解析接受三类 RUM 域，供分析搜索使用。运行说明见 [sop](sop.md)。
- `SData:Rum:CorsOrigins` 为可选全局上限，config/beacon/预检都与项目来源取交集；空列表不另设上限，非空且全部非法时拒绝所有来源。错误项目来源拒绝使用并记录项目键，不输出原始配置。
- `go/internal/rum/live*.go` 实现 SignalR JSON/WebSockets：协商只广告 WebSockets/Text，支持记录分隔符握手、Subscribe/Unsubscribe/SendClick、完成/错误、心跳与关闭；不实现 SSE、Long Polling、MessagePack 或流式 Hub 方法。
- 管理连接要求有效 JWT（Bearer 或 access_token）且同源；探针握手与每次 SendClick 按项目和全局来源复检。强制使用握手项目，缺 sessionId 用连接 id，缺时间用接收时间。直接点击在背压时仍广播并返回 false；beacon 只对已接受记录广播，不重复入队。
- 连接与待协商各最多 1024；协商令牌一次性、30 秒过期并绑定项目/来源/身份；每订阅者队列 64 条、最多订阅 128 项目，消息限 128 KiB，写超时 5 秒、握手 10 秒、读空闲 45 秒、心跳 15 秒。慢订阅者断开，不阻塞摄取；宿主取消和 Close 释放所有连接。
- `RumSource` 复用共享 `DayTables` 的一次有界目录查询。旧 `/sessions` 的额外摘要字段保留 C# 默认值，非实时状态估计；需要计数使用 `/sessions/search`。
- **尚未对齐**：冷归档查询；浏览器双端与断网重连尚未做本轮验收。探针只托管部署或开发目录中的既有真实构建产物；缺失、不可读、非普通文件返回 503，未修改或构建前端/C#。
