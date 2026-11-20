# Grafana 数据源对接清单（M7，Prometheus 兼容查询 API）

> 关联：docs/metrics/{sdd,bdd,tdd,sop}.md · 端点实现见 `src/metrics/SData.Metrics.Api/Controllers/PromController.cs`

SData 提供 Prometheus 兼容的查询端点，Grafana 以内置 Prometheus 数据源直连即可，无需插件。

## 1. 数据源配置

| 项 | 值 |
|---|---|
| Type | Prometheus |
| URL | `http://<sdata-host>:5300`（Grafana 自动在 URL 后追加 `/api/v1/...`） |
| Auth | Basic（SData 登录账户，admin 或 write 均可读）；Header `Authorization: Bearer <token>` 亦可 |
| Scrape interval | 依 Grafana 默认 |

## 2. 已支持能力（T711 子集）

| Grafana 查询形式 | 支持情况 |
|---|---|
| `rate(http_server_requests_total[5m])` | ✓（=(max-min)/range 秒数） |
| `sum/avg/min/max/count by (label) (expr)` | ✓（按 JSON 标签分组） |
| `topk(N, expr)` / `bottomk(N, expr)` | ✓ |
| 即时查询 `/api/v1/query` | ✓（区间末端值） |
| 区间查询 `/api/v1/query_range`（step） | ✓（按步长分桶 matrix） |
| label 匹配 `= != =~ !~` | ✓（JSON 标签提取 + 正则） |
| 二元运算 / 子查询 / histogram_quantile | 二期（返回 Prometheus 风格 error） |

## 3. 验收流程（e2e 待集成窗）

1. Grafana → Connections → Data sources → Add Prometheus，URL 填 SData 根地址。
2. Save & test（无需 /health 探测；鉴权失败返回 401 JSON）。
3. 新建 Dashboard 面板，查询 `rate(http_server_requests_total[5m])`，确认出图。
4. 若数据源探活失败：确认账户有读权限、`SData:Listeners` 未改动主端口、防火墙放行 5300。

## 4. 已知限制

- 查询走 metrics 天表（>24h 长区间经 rollup 表分派，见 docs/storage-layout.md rollup 节）。
- `without(labels)` 语义降级为按序列全标签分组（每序列独立返回，不做键剔除）。
- 无 instant-select 的 "lookback delta" 语义（取查询窗口末端值 arg_max 实现）。
