# auth — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: 站点品牌

  @AUTH-S11 @manual
  Scenario: 登录前后使用统一品牌标识
    When 用户打开登录页并登录进入平台
    Then 登录页、侧边栏与浏览器标签均显示同一猫咪品牌图片且保持原始配色

Feature: 认证与用户

  @AUTH-S1 @manual
  Scenario: 正确密码登录获得 token
    When 以 admin 与初始密码登录
    Then 返回 JWT、用户名与角色 admin

  @AUTH-S2 @manual
  Scenario: 错误密码返回 401
    When 密码错误
    Then 返回 401 业务错误

  @AUTH-S3 @manual
  Scenario: 用户列表全员可见，管理操作仅 admin
    When readonly/write 角色调用 GET /api/users
    Then 返回 200（团队共用，成员可在「用户管理」页修改自己密码）
    When readonly/write 角色调用 POST /api/users、DELETE /api/users/{id}、POST /api/users/{id}/password/reset、POST /api/users/{id}/role
    Then 返回 403

  @AUTH-S4 @manual
  Scenario: 修改密码后旧密码失效
    When 修改密码成功
    Then 旧密码登录失败、新密码登录成功

Feature: 角色与权限（单一超管，不开放注册）

  @AUTH-S5 @manual
  Scenario: 只读用户可查询不可写入
    When readonly 角色调用 GET /api/logs、POST /api/analytics/query（SELECT）
    Then 返回 200
    When readonly 角色调用 POST /api/dashboards（写操作）
    Then 返回 403

  @AUTH-S6 @manual
  Scenario: 可写用户可写入
    When write 角色调用 POST /api/dashboards
    Then 返回 200

  @AUTH-S7 @manual
  Scenario: 不允许创建超级管理员
    When admin 调用 POST /api/users 且 role=admin
    Then 返回 400 业务错误

  @AUTH-S8 @manual
  Scenario: 用户管理全流程
    When admin 创建只读用户、重置其密码、改权限为 write、最后删除
    Then 各步骤返回 200，重置后新密码可登录，改权限后写接口放行

  @AUTH-S9 @manual
  Scenario: 目标保护规则
    When admin 删除自己（DELETE /api/users/{self}）
    Then 返回 400「不允许删除当前登录账号」
    When admin 修改 admin 用户权限
    Then 返回 400「不允许修改超级管理员权限」

  @AUTH-S10 @manual
  Scenario: 旧角色数据自动迁移
    When 启动时存在 role='user' 的旧账号
    Then 启动日志 Warning 提示迁移数量，角色升级为 'write'（旧 token 需重新登录）
