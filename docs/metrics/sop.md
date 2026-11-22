# metrics — SOP

> 关联：[bdd](bdd.md) · [sdd](sdd.md) · [tdd](tdd.md)

## 配置

- 无 metrics 专属配置；查询跨度受 MaxQueryDays 约束。
- Grafana 对接清单：configs/grafana-note.md。

## 常见故障与处置

| 现象 | 处置 |
|---|---|
| `/api/v1/query` 返回 bad_data | 表达式超出 PromQL 子集（二元运算/子查询不支持）；错误消息含位置定位 |
| Grafana 探活失败 | 确认账户读权限；URL 填 SData 根（Grafana 自动追加 /api/v1）；防火墙放行 5300 |
| 长区间查询慢 | >24h 建议走 DSL search（自动 rollup 分派）；PromQL 直查原始天表受 MaxQueryDays 兜底 |
| 序列缺标签 | labels 为 JSON 字符串列，查询经 json_extract_string 提取；确认写入侧标签完整 |

## 验收流程

- Go：在 `go` 目录执行 `go test -tags duckdb_use_lib ./cmd/sdata -run TestPrometheusQueriesReturnRealSamples`，应验证选择器 70、区间速率 0.5、非法表达式 bad_data 和面板毫秒精度；请求需携带 Bearer 登录令牌。

1. `dotnet test tests/SData.Metrics.Tests`（解析 42 + 端到端 4）。
2. Grafana Prometheus 数据源对接（configs/grafana-note.md 4 步；无环境时 e2e 待集成窗）。

- 指标页时间缩放仅改变展示窗口；重新查询仍使用页面查询区间。共享组件排障见 [dashboard SOP](../dashboard/sop.md)。
