# 告警验证映射
关联：[场景](bdd.md) · [设计](sdd.md) · [运维](sop.md) · [测试](../../tests/SData.Store.Tests/AlertIntegrationTests.cs)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| ALT-S1 | AlertTests.State_*；AlertIntegrationTests.Save_Evaluate_Deduplicate_Resolve_AndDeliver | 通过（2026-11-19） |
| ALT-S2 | AlertIntegrationTests.Error_DoesNotResolve_AndOldVersionDeliveryIsCancelled | 通过（2026-11-19） |
| ALT-S3 | AlertIntegrationTests.Gate_*；DuckDbCancellationTests；AnalyticsSearchSqlTests.AlertCount_* | 通过（2026-11-19） |
| ALT-S4 | AlertIntegrationTests.Error_*、Delivery_RetriesAreBounded | 通过（2026-11-19） |
| ALT-S5 | SData.Database.Tests/AlertSchemaTests | 通过，数据库全量 19 项通过（2026-11-19） |
| ALT-S6 | ui/src/pages/search/__tests__/SearchAlertsModal.test.tsx | 3 项通过；真实浏览器验收待完成（2026-11-19） |

## 回归证据
- 最终告警定向后端 21 项通过（含性能样本、队列满载和投递顺序）；Search/告警弹窗定向 33 项通过。
- 前端全量 97 文件/664 项通过，最终定向 33 项通过；typecheck 最终复跑通过（alerts-typecheck-close.log），生产构建通过。期间 AnalyticsPage.tsx 的临时 JSX 语法错误曾阻塞，后续复跑已解除，未修改无关页面。
- 宿主独立 OutputPath 构建通过；默认输出被运行中的 SData 锁定，未停止既有服务。
- Store 全量 233 通过/1 失败（QueryDslTests.Execute_MetricsBucketAvg），该用例单独复跑通过；不宣称全量全绿。
- 证据位于 `.artifacts/alerts-*.log`，仅本地验证产物。

## 性能样本
`AnalyticsSearchSqlTests.AlertPerformance_MixedSearchAndIngest_Sample`，DuckDB threads=2，初始 10 万行，每组 30 次搜索+3000 行摄取：
- 无告警：搜索 P95 2.48ms，摄取 32509 行/秒。
- 串行有界告警：搜索 P95 2.62ms，摄取 31464 行/秒。
- 非基准实验：样本小、共享主机、非同规模生产数据；不可外推为零性能影响。
