# 文档标准（DOC-STANDARD）

SData 文档采用「分层 + 四件套」模式，目标是：**上层索引、下层承载内容、跨层不复制**（重复即缺陷，用链接代替）。

## 分层

| 层 | 位置 | 内容 |
|---|---|---|
| L0 | `docs/README.md` | 导航总图：全局规范 + 模块文档索引 + 回归入口 |
| L1 | `docs/*.md`、`ui/docs/frontend-conventions.md` | 跨模块规范（CQRS 约定、存储布局、文档标准本身） |
| L2 | `docs/<module>/{sdd,bdd,tdd,sop}.md` | 模块四件套 |
| L3 | `tests/`、`local-dev/`（e2e 脚本） | 证据层：被 tdd 引用，从不复制内容 |

## 四件套契约（每个模块的 L2 文档）

- **sdd.md** — 设计说明：目标、组件、数据布局、关键决策、已知问题。≤120 行。
- **bdd.md** — 纯 Gherkin 业务场景，中文业务语言，不含 HTTP/代码细节。场景标签 `@<模块缩写>-S<n>`（id 永不复用），第二标签 `@auto:e2e` / `@auto:xunit` / `@manual`；每个场景 ≥2 步且以 `Then` 结束，单场景 ≤8 步。
- **tdd.md** — 验证映射表（强制表格）：`| 场景 | 验证物 | 结果（日期） |`，把 bdd 场景映射到测试/脚本证据。≤100 行。
- **sop.md** — 运维手册：配置、常见故障与处置、验收流程。≤100 行。

每个文件以「关联」块开头（链接同模块其余三件 + 上游模块 + 证据脚本）。

## 变更流程

代码变更 → 更新 bdd 场景 → 运行 tdd 映射的验证并回填日期 → 同步 sdd 的决策与 sop。

## 现状说明

本期为脚手架交付：核心模块（store / ingest / logs / analytics / dashboard / auth）提供完整四件套或 sdd+bdd；其余模块提供 sdd 骨架。bdd 场景的自动验证以 `tests/` 下 xUnit 项目为证据（`@auto:xunit`），e2e 脚本随后续迭代补充。
