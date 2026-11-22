# auth — TDD

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [sop](sop.md)

| 场景 | 验证物 | 结果（日期） |
|---|---|---|
| @AUTH-GO1 | `go/internal/auth/auth_test.go` | 2026-11-22：定向通过；9007199254740993 无损往返，无效 JWT 不阻断独立摄取认证且管理路由仍返回 401 |
| @AUTH-S1/S2 | 手工 curl（登录成功/失败，2026-11-15 通过） | 通过 |
| @AUTH-S3/S4 | 手工 API 验证（待 e2e 脚本固化）；S3 场景 2026-11-16 修订为「列表全员可见、管理操作仅 admin」，前端成员视角由 `ui/src/pages/users/__tests__/index.test.tsx` 覆盖 | 通过（2026-11-15，S3 修订待手工复验） |
| 密码哈希/审计 | `tests/SData.Database.Tests.DatabaseTests.AuditFields_AutoFilled_And_UniqueUsername` | 通过（2026-11-15） |
| @AUTH-S11 | `ui/src/layouts/components/__tests__/AppLogo.test.tsx`、`AppSider.test.tsx`；原图与构建产物 SHA-256 比对；`npm --prefix ui run test`、`npm --prefix ui run build` | 组件 3/3、完整前端 376/376、构建与文件一致性通过（2026-11-18）；浏览器视觉验收未执行 |
