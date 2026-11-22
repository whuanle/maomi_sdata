# CQRS 与分层约定（后端编码标准）

SData 后端的编码标准。硬规则的背景见根 `AGENTS.md`，本文给出落地细节。

## 模块三件套

```
SData.{Domain}.Shared   请求/响应模型 + {Domain}SharedModule
SData.{Domain}.Core     Handler + 领域服务 + {Domain}CoreModule（[InjectModule] 链）
SData.{Domain}.Api      Controller + {Domain}ApiModule
```

- `Api` 项目只引用自身 `Shared` 与 `Infra.Api`；需要调用其它域时通过接口（接口定义在本域 `Shared`，实现在对方 `Core`，如 `IIngestIngestor`/`IIngestKeyValidator`/`IOtlpParser`）。
- `Core` 的模块类用 `[InjectModule<自身SharedModule>]` + `[InjectModule<自身ApiModule>]`（+ 依赖域模块）声明依赖链。
- 服务注册优先 Maomi 特性；跨类型注册（接口映射、集合注册）在 `{Domain}CoreModule.ConfigureServices` 手写。

## 命令/查询

- 命令 `{Action}{Entity}Command : IRequest<TResponse>`（Mediator，`using Mediator;`）；查询 `Query{Entity}Command`；分页查询继承 `PagedParameter`，响应 `PagedResult<T>`。
- **每个请求模型实现 `IModelValidator<T>`**（`SData.Infra.Services`）；验证在 `MediatorPipelineBehavior` 中执行（进入 Handler 前失败返回 400）。
- Handler 的 `Handle` 返回 `ValueTask<TResponse>`；控制器注入 `IMediator`，`Send` 返回 `ValueTask<TResponse>`（MVC 动作直接返回时签名用 `ValueTask<T>`）。
- 响应模型放 `Queries/Responses/`（查询）或与命令同文件（简单命令）。
- Handler 命名 `{Command}Handler`，构造注入 `DatabaseContext` / `I{Xxx}Store` / `IIdProvider` 等。

## 用户身份

- Controller 侧：`_userContextProvider.SetUserContext(command)`（仅对 `IUserIdContext` 模型）；角色门禁 `[Authorize(Roles="admin")]`。
- Handler 侧：只读 `command.ContextUserId / ContextUserRole`，不注入任何用户上下文服务。

## 错误

- `BusinessException.Throw(statusCode, message)`；statusCode 直接作为 HTTP 状态码（400 参数、404 缺失、409 冲突、401 未认证）。
- `BusinessExceptionHandler`（IExceptionHandler）统一渲染 `BusinessValidationResult`。

## 数据访问规则

### SQLite（SData.Database.*）
- `DatabaseContext` 即仓储；审计与软删除由框架事件自动处理（见 `DatabaseContext.cs`）。
- 无迁移框架：`EnsureCreated` + `SchemaReconciler`（只增列）。新实体：加 `Entities/` + `DatabaseContext` DbSet + `DatabaseContext.Configuration.cs` 映射 + `SchemaSync apply` 修补存量库。

### DuckDB（SData.Store.*）
- 写入只经 `IIngestBuffer`；查询经 `ILogStore/ITraceStore/IMetricStore/ITelemetryStore`；分析经 `IAnalyticsExecutor`。
- **SQL 一律参数绑定**；按天表名仅由 `TimeTables.Name()` 生成；跨天查询用 `BuildUnion`（外部 WHERE + LIMIT 由各 Store 组装，同样参数化）。

## 验证清单（PR 自检）

1. 新端点是否有 `IModelValidator`？2. 用户模型是否走 `IUserIdContext` + Controller 注入？3. SQL 是否全参数绑定？4. 时间是否 `DateTimeOffset`？5. 是否更新了对应模块的 bdd/tdd？
