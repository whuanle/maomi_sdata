# auth — SDD

> 关联：[bdd](bdd.md) · [tdd](tdd.md) · [sop](sop.md) · 上游：[存储布局](../storage-layout.md)

## 目标

内网单机的最小可用认证：本地账号 + JWT(RSA)，接入端点用 API Key 隔离（见 ingest）。

## 组件

- `UserEntity`（SQLite，PBKDF2 210k 迭代哈希）；首次启动 seed admin（密码来自 `Auth:InitialAdminPassword`）。
- `TokenService`：RSA2048 签名 JWT，issuer/audience = `SData:Server`，有效期 `TokenExpireMinutes`（默认 720）；RSA 密钥首次启动自动生成于 `configs/rsa_private.key`。
- `ConfigureAuthorizaModule`：JwtBearer 校验（ClockSkew=0）；`/api/ingest/*` 走 API Key 过滤器而非 JWT。
- 品牌图片统一为 `ui/public/logo.png`（从 Maomi.MQ 的 `package.png` 原样复制）；`AppLogo` 供登录页与侧边栏共用，浏览器图标引用同一图片，不随主题重绘。

## 决策

- 无刷新 token：内网场景有效期 12h，过期重登。
- 不开放注册；角色三值（`SDataRoles` 常量，`SData.Infra.Shared`）：`admin`（超级管理员，种子唯一）/ `write`（可写）/ `readonly`（只读）。启动时旧 `user` 角色自动升级为 `write`（DatabaseSqliteModule 种子阶段 SQL 修补）。
- 写权限强制：Dashboard/Iot/Analytics 的 POST/PUT/DELETE 端点 `[Authorize(Roles = SDataRoles.AdminOrWrite)]`；查询/SELECT 分析保持 `[Authorize]`（只读用户可用）。
- 用户管理（团队共用）：列表 `[Authorize]` 登录即可（全员可见，成员可改自己密码）；创建（role 仅 write/readonly）、删除（禁止删自己/删 admin）、重置密码（≥8 位）、改权限（admin 用户不可改）均 `[Authorize(Roles = Admin)]`。目标保护规则放 Handler。
