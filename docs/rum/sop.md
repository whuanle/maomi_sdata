# rum — SOP

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sdd](sdd.md)

## 接入

1. 登录 `/sessions`，管理员直接点击工具栏「创建项目」，设置项目标识、名称与性能/回放采样率；创建成功后自动选中新项目。
2. 点击「接入配置」复制当前项目的接入代码，使用实际 SData origin 与项目标识；部署前执行 `npm --prefix ui run build`（含 `build:probe`）。
3. 探针端点 `/rum/probe.js` 返回真实 IIFE；缺构建产物返回 503。单独构建用 `npm --prefix ui run build:probe`。
4. 默认遮蔽输入框；敏感区域添加 `sdata-private`。接入方负责隐私告知、用户同意与数据留存策略。
5. 管理员在创建项目或「接入配置 → 允许站点」填写完整 Origin（如 `https://app.example.com` 或 `http://localhost:3000`），每行一个；协议、主机与端口精确匹配，不支持通配符或隐式子域。留空禁止采集。
6. **升级前先补齐所有项目允许站点**：旧裸域名需补协议/端口；未知、删除、空白名单项目及缺失/非法来源的 config、beacon、实时上报均拒绝。不能只升级前端；部署新后端后严格策略生效。
7. `SData:Rum:CorsOrigins` 是可选额外全局上限，非空时与项目白名单取交集，在 `appsettings.json` 的 `SData:Rum:CorsOrigins` 中设置并重启；不配置则按项目白名单。开发代理保留 Host，转发 `/rum` 与 `/hub`；反代应保留外部 Host/协议，不能用不可信转发头绕过检查。
8. Origin 优先，仅缺失时回退 Referer；两者均缺失则拒绝。脚本公开可下载；来源检查不等同于认证，非浏览器可伪造头，仍需网络边界及独立限流防护。

```html
<script src="http://<sdata-host>:5300/rum/probe.js"></script>
<script>
SDataRum.init({ serverUrl: 'http://<sdata-host>:5300', projectKey: 'rum_demo' });
</script>
```

## 检索与回放

- 主入口 `/sessions`，旧 `/rum` 带原查询参数兼容跳转。
- 工具栏项目选择器在左侧保持紧凑宽度；搜索框使用剩余空间。错误、回放与排序筛选并排展示，窄窗口下控件自然换行。
- 搜索为会话/域名/最近页面/UA 的不区分大小写包含匹配，不支持 Lucene；错误与回放状态独立筛选。
- 时间窗口为 `[from,to)`，上限 30 天。首末活动、时长与计数均为窗口内口径，不是完整生命周期。
- 会话详情显示回放及前 1000 条事件，复制链接保留项目、时间和会话；返回列表保留条件。
- 图表与事件搜索样本复用播放器，携带项目和执行时的绝对区间；不同项目相同 sessionId 不混放。
- 损坏分片告警并跳过；播放器加载失败保留原始事件预览，查询失败可重试。没有分片不生成模拟回放。

## 采集与实时

- 采样配置 `/rum/v1/config?projectKey=` 对已建项目下发保存值；pageview/error 全采，其他性能事件按性能采样率，录制启动按回放采样率。
- 上报端点 `/rum/v1/beacon`，sendBeacon 优先、fetch keepalive 回退；批满或定时 flush，失败队列重试超限会丢弃，不能保证断电或页面强制关闭零丢失。
- `realtime: true` 开启 `/hub/rum?projectKey=`，SendClick 返回 false 或失败回落批量；管理端 JWT 订阅，重连后重新 Subscribe。
- 文档坐标字段为 pageX/pageY/docW/docH；点击目标 selector 优先，旧 targetId 回退。

## 排障与验收

- 项目列表失败：确认 `/api/rum-projects`（不是双 `/api`）、登录状态与服务版本；写操作仅 admin。
- 探针 503：重新 build:probe 并将 `wwwroot/rum/probe.js` 随部署发布；配置跨域与正确 serverUrl。
- 无会话：确认项目、采集时间、采样率及浏览器请求；回放需包含完整快照，时间窗过窄可能无法播放。
- 使用独立数据库和测试项目验证“真实探针 → 点击/DOM 变化 → 入库 → 列表 → 回放”，不向参考 HyperDX 写数据。
- 本次验收实例使用 5391 独立端口，未停止原 5300 服务；结果与尚未完成项见 [tdd](tdd.md)。
- Sessions 冷归档：设置页填写本地保留天数，包含会话、事件、回放。归档后沿用原查询入口和时间范围；桶目录分别为 rum_session、rum_event、rum_replay。先部署新后端再保存策略，不能只升级前端。

## Go 运行与限制

1. 启动增量补充 SQLite 项目列，在生产者启动前启用共享 RUM Appender；初始化失败阻止启动，关闭服务排空已接受的缓冲。旧 Go SQLite 占位时序表不再用于采集或查询，不自动迁移历史占位数据。
2. `/api/rum-projects` 项目列表和查询要求 JWT，新增/修改/删除仅 admin；普通 RUM 查询沿用原路径、分页和 `[from,to)` 时间窗口。
3. 在 `appsettings.json` 的 `SData.Rum.CorsOrigins` 配置完整 Origin 字符串数组，重启生效；非空时与项目白名单取交集，空数组表示仅项目白名单。全局配置不能扩大任何项目权限，非法项目来源会记录日志并拒绝。
4. config/beacon 支持跨域预检。预检先应用全局上限，再检查未删除项目的来源集合；实际请求再次检查目标项目。错误来源不返回 CORS 放行头且不入队。
5. 正式部署优先使用运行目录 `web/rum/probe.js` 或 `wwwroot/rum/probe.js`。开发模式兼容 `ui/dist`、`ui/dist-output` 及 `src/SData/wwwroot`（也支持运行目录上一层），只读取其中 `rum/probe.js`；后者是既有探针构建的真实输出位置。缺文件、无读取权限或非普通文件返回 503，不构建或返回替代脚本。
6. `/hub/rum` 的 SignalR 协商只提供 WebSockets/Text；要求代理允许 WebSocket Upgrade。探针带 projectKey，管理端带 JWT 并来自服务同源站点；项目删除或移除来源后，已连接探针下一次点击同样被拒绝。缓冲满时 SendClick 返回 false，SDK 回落批量上报；慢订阅者断开，SDK 可重连并重新订阅。冷归档读取仍未实现。
7. 验证：在 `go` 目录执行 `go test -tags duckdb_use_lib ./internal/config ./internal/rum ./internal/store ./cmd/sdata -run 'Rum|InitPreserves|OriginsReject'`，设置已验证的 CGO/DuckDB 本地库环境；测试使用独立数据，不改现有运行库。

### 实时宿主接线

- `App.RumLive *rum.LiveHub`，初始化 `rum.NewLiveHub(background, db.SQL, storeService, authService, cfg.Rum.CorsOrigins)`；后台 context 取消可关闭连接，`App.Close` 仍须在 Store/DB 关闭前调用 `RumLive.Close()` 等待退出。
- `r.Handle("/hub/rum", app.RumLive)` 和 `r.Handle("/hub/rum/*", app.RumLive)` 包含协商、升级与预检，不能再绑定 501 占位处理器。
- beacon 路由使用 `r.With(app.RumLive.PublishBeacons).Post("/rum/v1/beacon", app.handleRumBeacon)`；中间件仅附加已接受点击的广播回调，不增加第二次写入。
