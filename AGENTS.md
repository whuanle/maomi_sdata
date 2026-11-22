# AGENTS.md — SData 贡献者规范

SData 是面向内网单机部署的高性能观测/数据分析服务：**DuckDB 承载海量时序数据（日志/链路/指标/物联网遥测），SQLite 仅存元数据（用户/设备/仪表盘/保存的查询）**。本文件是后端贡献者的最高规范，前端规范见 `ui/AGENTS.md`，文档规范见 `docs/DOC-STANDARD.md`。

## 1. 技术栈与架构

- .NET 10 模块化单体（Maomi.Core 模块框架 + Mediator（martinothamar/Mediator 源生成器）CQRS + EF Core(SQLite) + DuckDB.NET）。
- **单一项目 `src/SData/SData.csproj`**（2026-11 由 47 个拆分项目合并而来，方便发布、避免多份 pdb/xml）。源码按 `{Domain}/{Layer}/` 目录组织：`{Domain}/Shared/`（请求/响应模型）、`{Domain}/Core/`（Handler/服务）、`{Domain}/Api/`（Controller）。命名空间保持拆分时期的形式（`SData.{Domain}.Shared/Core/Api`）。
- 依赖方向仍严格单向（现为命名空间约定，不再由程序集边界强制）：`Api → Shared`，`Core → Api + Shared`；跨域只使用对方 `Shared`（或经接口）。
- 宿主通过 `[InjectModule]` 聚合全部模块（`MainModule`）；横切配置（MVC/认证/Mediator/OTel）在 `src/SData/Modules/`。

## 2. 模型与验证

- 请求模型命名 `{Action}{Entity}Command` / `Query{Entity}Command`，响应 `{Command}Response` + `{Entity}Item`。
- **每个请求模型必须实现 `IModelValidator<T>`**（`static abstract void Validate(AbstractValidator<T>)`，位于 `SData.Infra.Services`），由 `InfraCoreModule.TypeFilter` 自动注册、`MediatorPipelineBehavior` 在进入 Handler 前执行；Handler 注册由 Mediator 源生成器在 `AddMediator()` 时自动完成（生命周期 Scoped，见 `ConfigureMediatorModule`）。
- 用户身份只在 Controller 中注入：控制器调用 `_userContextProvider.SetUserContext(command)`；Handler 永不注入 `IUserContextProvider`，用户身份通过 `IUserIdContext` 携带在请求模型里。
- 角色控制只放 Controller（`[Authorize(Roles = "admin")]`）；目标保护规则（如不允许删自己）放 Handler。

## 3. 数据访问（两条存储线，禁止混用）

### SQLite（元数据，EF Core）
- Handler 直接注入 `DatabaseContext`（EF 即仓储），不建通用 Repository 层。
- 实体继承 `FullAuditedEntity`：`CreateUserId/CreateTime/UpdateUserId/UpdateTime/IsDeleted` 由框架自动填充（Controller 未登录时为 0），**禁止手工赋值**；软删除全局过滤器自动生效，**禁止写 `.Where(x => x.IsDeleted == 0)`**。
- 时间一律 `DateTimeOffset`；id 用 `IIdProvider.GetLongId()`（雪花）。
- 建库走 `EnsureCreated + SchemaReconciler`（只增不删的增量修补），不用迁移框架；表/列名 snake_case（在 `DatabaseContext.Configuration.cs` 配置）。

### DuckDB（观测/时序数据）
- 一切写入走 `IIngestBuffer`（内存有界缓冲）→ `IngestWriterService` 批量 Appender 落库。**禁止逐条 INSERT、禁止在请求线程同步写 DuckDB。**
- 按天分表：`logs_yyyymmdd` / `traces_yyyymmdd` / `metrics_yyyymmdd` / `iot_yyyymmdd`，表名与建表 SQL 由 `TimeTables` 统一管理；保留策略 `RetentionService` 直接 DROP 过期表。
- **查询 SQL 的所有外部输入必须参数绑定（$name + DuckDBParameter），禁止字符串拼接/format/f-string 组装**；表名只能来自 `TimeTables.Name()`（内部日期，非用户输入）。
- 用户提交的分析 SQL 只允许经 `IAnalyticsExecutor`（只读、单语句 SELECT/WITH、行数上限）执行，其它通道一律禁止。
- 时间类型 TIMESTAMP（微秒精度），级别/状态等用字符串枚举值。

## 4. 错误与日志

- 业务错误抛 `BusinessException` 并显式设置 StatusCode（缺省 500 会破坏前端处理），消息为人类可读中文（后续 i18n 由 `tool/I18nScanner` 扫描生成资源）。
- Handler 层用 `ILogger<T>` 结构化日志；禁用 Console.WriteLine。

## 5. 可观测性

- 内部链路：`AppConst.ActivitySource`（Mediator 管线自动埋点）；自身指标/链路固定经本地缓冲写入本机，不可关闭或修改；`SData:OTLP` 仅控制可选的额外外部出口。
- 服务状态只经 Monitor 模块（/health、/metrics、/api/monitor/status），不要在各模块自造状态接口。

## 6. 文档与流程

- 代码变更 → 同步更新 `docs/{module}/bdd.md` 场景 → 跑对应验证 → 更新 sdd/sop。文档分层与四件套规范见 `docs/DOC-STANDARD.md`。
- 新模块脚手架：在 `src/SData/{Domain}/` 下建 `Shared/`（含 `{Domain}SharedModule`）+ `Core/`（含 `[InjectModule]` 的 `{Domain}CoreModule`）+ `Api/`（含 `{Domain}ApiModule`），在宿主 `MainModule` 上追加 `[InjectModule<...>]`。

## 7. 工具链

- `tool/SchemaSync`：`script`（导出 SQLite 建库脚本）/ `apply`（增量修补）/ `duck`（应用 DuckDB 内嵌 DDL）。
- `tool/I18nScanner`：扫描 BusinessException 消息生成 i18n 资源（`--src --out --dry-run --report`）。
- 部署：单容器（宿主内嵌前端静态资源，同源），见 `deploy/`、`Dockerfile`。
