# logs — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @LOGS-S7 邻域锚点与属性匹配 | `surroundingContext.test.ts`、`SurroundingContextTab.test.tsx` | 微秒时间窗、主机/嵌套属性参数绑定、查看不重定位与显式重定位通过（2026-11-18）；实际页面联动待验收 |
| @LOGS-S6 | `ui/src/pages/search/__tests__/LogBodyPreview.test.tsx`（短消息、溢出展开/收起、全文复制）+ `LogDetailDrawer.test.tsx` | 通过（2026-11-17，ui 全量 178/178、生产构建通过；真实浏览器布局待登录验收） |
| @LOGS-S4/S5 | `ui/src/pages/search/__tests__/SurroundingContextTab.test.tsx` + `surroundingContext.test.ts` + `LogDetailDrawer.test.tsx` | 通过（2026-11-17，ui 全量 176/176、生产构建通过；DuckDB `AnalyticsSearchSqlTests.ExecuteSearchAsync_ContextPages_ProbeAndAppend` 5/5；浏览器无登录态，待验收） |
| @LOGS-S1/S2/S3 | `tests/SData.Store.Tests.Log_WriteThenQuery_RoundTrip`（底层）+ 手工 API 验证（2026-11-15 curl 通过） | 通过 |
| @LOGS-SEARCH1/2 | `QueryDslTests`（logs 域编译例 + 端到端执行例） | 通过（2026-11-15） |
| @LOGS-SEARCH3 | `QueryDslTests.Logs_JsonGte_UsesTryCastAndNumberBind` | 通过（2026-11-16，统一搜索页） |
| @LOGS-UI-JSONDISC1/2 | `tests/SData.Store.Tests/AnalyticsSearchSqlTests`（JsonNestedPathParam 互不串查 / ChildKeysSql 子属性枚举 / JsonProjectionQuotedAlias 列别名，真实 DuckDB） | 通过（2026-11-18，AnalyticsSearchSqlTests 28/28） |
| @LOGS-UI-JSONDISC3 | `ui/src/pages/search/__tests__`（jsonFields.test 路径解析/绑定、lucene.test 括号路径编译、FilterTree.test 子属性树、SearchPage.test 发现树选列→别名查询、surroundingContext/LogDetailDrawer 参数化回归） | 通过（2026-11-18，ui 全量 241/241、生产构建通过；浏览器验收待登录态） |
| @LOGS-UI-SUGGEST1 | `ui/src/pages/search/__tests__/WhereSuggestInput.test.tsx`（6 例：字段/值/JSON 子属性建议、Enter 采纳截断运行、无弹层 Enter 运行 + Esc、SQL 关键字、IME 跳过）+ `lucene.test.ts` resolveTokenContext 5 例 + SearchPage 集成回归 | 通过（2026-11-18，ui 全量 254/254、生产构建通过；浏览器验收待登录态） |
