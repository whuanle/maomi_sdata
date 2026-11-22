# auth — SOP

> 关联：[sdd](sdd.md) · [bdd](bdd.md) · [tdd](tdd.md)

- Go 切换后需要重新登录；SQLite 的 `go_jwt_secret` 仅内部使用，Settings API 不输出。运行 `go test ./internal/auth` 验证大整数身份和路由授权边界；采集器 Key 与用户 JWT 不可混用。

- 首次启动务必用 `Auth:InitialAdminPassword` 登录后立即改密（「用户管理」页自己行的「修改密码」，等价 `POST /api/auth/password/change`）。
- RSA 私钥丢失 = 所有 token 失效（重新登录即可）；私钥泄露需删除 `configs/rsa_private.key` 重启（全员重新登录）。
- 用户管理：`GET /api/users`（登录即可）；`POST /api/users`、删除/重置密码/改权限（仅 admin）。
- token 无服务端状态，无法单独吊销；紧急情况删除用户即可。
- 品牌验收：构建前端后确认登录页、侧边栏和浏览器标签均显示猫咪 Logo；若图标仍旧，清除浏览器缓存并确认部署目录包含 `logo.png`。
