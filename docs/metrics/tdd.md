| @METRICS-PROM1 | `tests/SData.Metrics.Tests/PromqlExecutorTests.Rate_EndToEnd_ReturnsPerSecondDelta` | 通过（2026-11-15，Metrics.Tests 46/46） |
| @METRICS-PROM2 | `PromqlExecutorTests.SumBy_GroupsByLabel` / `Topk_And_RangeQuery` | 通过（2026-11-15） |
| @METRICS-PROM3 | `Topk_And_RangeQuery`（matrix）+ `InvalidExpression_ThrowsParseError` | 通过（2026-11-15） |
| @METRICS-S1 | `ui/src/design-system/__tests__/charts.test.tsx`、`Chart.test.tsx` | 2026-11-18：全量前端 209/209，构建通过；指标页缩放的浏览器交互验收待完成 |
| @METRICS-GO1 | `go/cmd/sdata/prometheus_parity_test.go` | 2026-11-22：真实临时 DuckDB 定向通过，selector/aggregate=70、rate=0.5、bad_data 与面板毫秒精度；PromQL 仅对齐当前 C# 子集 |
