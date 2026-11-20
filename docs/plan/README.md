# 并行实施计划（多会话协作协议）

## 关联

- [architecture.md](../architecture.md) — 功能架构与里程碑 M1–M7
- [TASKS.md](TASKS.md) — 任务卡与接口契约（任务详情以它为准）
- [STATE.md](STATE.md) — **共享状态账本（唯一事实源，每个会话开工前必读必写）**
- 根 `AGENTS.md` — 编码硬规则；`docs/DOC-STANDARD.md` — 文档四件套

## 三份文件的分工

| 文件 | 谁写 | 何时写 |
|---|---|---|
| `README.md`（本文件） | 人/主会话一次性维护 | 协议变更时 |
| `TASKS.md` | 主会话维护；契约变更需在 STATE.md 决策日志登记 | 新任务/契约变更时 |
| `STATE.md` | **所有工作会话随时写** | 认领/完成/阻塞/决策/构建健康变化时 |

## 会话工作协议（硬规则）

1. **开工三步**：读 `STATE.md` → 在 `TASKS.md` 找到本组第一个「状态=todo 且依赖全部 done」的任务 → **立即**在 `STATE.md` 把该任务置 `claimed` 并写上会话 ID。不认领不动代码；一次只认领一个任务。
2. **文件所有权**：只允许改动任务卡「文件」栏声明的文件 + 第 5 节热文件（且仅限增量追加）。需要动其他文件 → 换任务或在 STATE.md 冲突区登记后等主会话裁决。
3. **构建互斥**：并行会话共享 bin/obj，构建可能互相占用。构建报「文件被占用/并发冲突」时等待 60s 重试，**不得因并发构建错误修改代码**。完成一个任务至少跑一次 `dotnet build SData.slnx`。
4. **DoD 全过才算 done**：见第 6 节。完成 → `STATE.md` 置 `done` + 备注；若启用了 git，`git commit -m "Txxx: 摘要"`。
5. **构建破坏责任**：发现 build 红了且不是自己的改动 → 在 STATE.md 构建健康区登记 `broken-by:<任务号>`，不修他人代码；责任会话修复。责任会话已结束 → 标记 `orphan`，任意会话可认领修复。
6. **收尾纪律**：会话结束前，名下 `claimed/in_progress` 任务要么完成、要么退回 `todo` 并写明原因，避免死锁。
7. **契约先行**：跨任务接口/协议/实体只认 `TASKS.md` 契约区（C1–C6）。实现方与消费方都按契约编码；要改契约 → 先在 STATE.md 决策日志登记，再改 `TASKS.md`，并通知会受影响的已完成任务（在冲突区 @ 任务号）。
8. **诚实汇报**：验证失败如实写进 STATE.md 备注与 tdd 证据，禁止标记未验证的 done。

### 任务状态机

```
todo → claimed → in_progress → done
                ↘ blocked（依赖/环境缺失，写明原因）
done 发现回归 → 回退 todo（备注写明回归来源）
claimed 会话消失且超 24h → orphan（任意会话可接管）
```

### 认领写法（STATE.md 账本行）

`| T110 | claimed | 组A-20261115-2105 | 21:05 | | 原因/备注 |`

会话 ID = `组别-yyyyMMdd-HHmm`，取自启动词里的组别。

## 热文件纪律（append-only，每次只加不改不删）

多任务共同触及的文件，只允许**增量追加**；编辑前必须重读（Edit 工具对过期内容会直接失败，失败即重读重试，不要强行覆盖）：

| 热文件 | 规则 |
|---|---|
| `Directory.Packages.props` | **T000 一次性预声明全部计划包版本后封板**；之后仅 T710（PromQL 解析器选型）可再改 |
| `SData.slnx` | 新增项目只追加项目条目（T610 等） |
| `src/SData/MainModule.cs`、`src/SData/Modules/*` | 每个波次指定**唯一拥有者任务**做接线（W1=T112，W2=T310/T311 完成后由 E 组接线，W3=T511/T711），其余任务禁止触碰 |
| `src/database/.../DatabaseContext.cs` | DbSet 每实体追加一行；实体文件各自新建 |
| `configs/system.yaml`、`system.example.json` | 配置键追加；键名以契约 C4 为准 |
| `ui/src/router/index.tsx`、`ui/src/layouts/components/AppSider.tsx`、`ui/src/i18n/*` | 路由/菜单/i18n 条目追加（zh-CN 与 en-US 成对） |
| `docs/README.md` | 导航追加 |
| `docs/plan/STATE.md` | 表格行内更新 + 日志区追加，不删行 |

## 每个任务的 DoD（Definition of Done）

1. `dotnet build SData.slnx` 全绿（含其他会话已完成代码）。
2. 任务卡指定的测试项目全绿（新增逻辑必须有 xunit 测试；遵循根 AGENTS.md：请求验证器、审计字段免赋值、DuckDB 参数绑定、写入走 IIngestBuffer）。
3. 前端任务：`cd ui && npm run test` 全绿 + 新页面 `__tests__` 冒烟 + i18n 双语成对。
4. 文档：更新对应 `docs/{module}/bdd.md` 场景（新模块按 DOC-STANDARD 建四件套）；涉及 SQL/API 变更同步 `storage-layout.md`/`ui-pages.md`。
5. `STATE.md` 置 done + 一行验证证据（如「build ✓ / Ingest.Tests 43/43 ✓ / e2e 见 tdd」）。
6. （启用 git 后）独立 commit，message 以任务号开头。

## 波次与并行度

前置：**W0 完成前不开并行**（包版本封板 + 契约冻结，否则并行会互相踩）。

| 波次 | 组 | 任务 | 硬依赖 | 并行会话 |
|---|---|---|---|---|
| W0 | 地基 | T000, T010 | — | 1 |
| W1 | A(M1 OTLP) | T110→T111→T112→T113 | W0 | 4（A/B/C/D 各一） |
| W1 | B(M2 ES Bulk) | T120→T121→T122 | W0 | ↑ |
| W1 | C(M4 MQTT) | T140→T141→T142 | W0 | ↑ |
| W1 | D(M6 RUM 后端) | T610→T611 | W0 | ↑ |
| W2 | E(M3 DSL/大屏) | T310→T311→T312→T313 | W0（T313 可先行） | 2–3（E/G/I 各一） |
| W2 | G(RUM 前端+查询) | T612→T613→T614→T615 | T610/T611 | ↑ |
| W2 | I(总览聚合) | T314 | — | ↑ |
| W3 | F(M5 S3 冷存) | T511→T510→T512→T513 | T311（查询面已迁 DSL） | 3–4（F/H/G/最后 I） |
| W3 | H(M7 PromQL) | T710→T711→T712 | T312 | ↑ |
| 末 | I(集成收尾) | T900 | 全部 done/blocked 豁免 | 最后一个会话 |

组内任务有顺序依赖，组间完全并行。G 组可提前到 W1 尾部（T610 done 后）。

## 会话启动词（开新会话直接粘贴）

模板（把「组X」换成下表对应组）：

```
阅读 F:\workspace\maomi_sdata\docs\plan\README.md、TASKS.md、STATE.md。
本次会话身份 = 组A。严格按协议执行：认领组内第一个前置满足的任务，遵守文件所有权与热文件纪律，
按 DoD 收尾并更新 STATE.md。开始。
```

| 会话 | 组 | 波次 | 范围 |
|---|---|---|---|
| 会话 1 | 地基 | W0 | T000 → T010（完成即转任 A 组） |
| 会话 2 | B | W1 | M2 ES Bulk 接入 |
| 会话 3 | C | W1 | M4 MQTT 接入 |
| 会话 4 | D | W1 | M6 RUM 后端骨架（W2 转任 G 组继续） |
| 会话 5 | E | W2 | M3 查询 DSL + rollup + 大屏（W3 转 F 组做冷存储） |
| 会话 6 | H | W3 | M7 PromQL 子集 + Grafana 验收 |

## 验证命令速查

```bash
dotnet build SData.slnx
dotnet test tests/SData.Store.Tests        # DuckDB 全链路
dotnet test tests/SData.Database.Tests     # SQLite 审计/软删除/schema
dotnet test tests/SData.Ingest.Tests       # 摄取解析 + 缓冲
cd ui && npm run test && npm run build
dotnet run --project tool/SchemaSync -- duck    # 手动补跑 DuckDB DDL
dotnet run --project tool/I18nScanner -- --src --out --dry-run
```

## 外部环境验收（可延后到集成窗，不阻塞 done）

T113（OTel Collector）、T122（Filebeat）、T142（MQTTX）、T513（MinIO）、T712（Grafana）需要外部工具；无环境时：代码级测试（构造等价报文）全过即可标 done，备注「e2e 待集成窗」，由 T900 统一补验。
