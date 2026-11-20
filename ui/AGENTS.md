# AGENTS.md — SData 前端规范

React 19 + TypeScript + Vite + **Tailwind CSS 4 + Radix 原语（自研 design-system v2）** + zustand + react-router 7 + echarts。antd 已移除：组件层全部归仓库所有，样式走 Tailwind 语义色 + CSS 变量主题，目标观感对标 HyperDX（暗色高密度观测台）。本文件是前端贡献者的硬规则，违反会在 review 中被打回。

## 硬规则

1. **组件一律从 `@/design-system` 引入**（Button/Input/Select/Modal/Drawer/Popconfirm/DataTable/Page/QueryBar 等 v2 全家桶），禁止再引入 antd 或其它 UI 库；`@/design-system/legacy` 是迁移期过渡产物，已随 antd 删除，禁止引用。
2. **样式只用 Tailwind 语义色与主题 token**：`bg-surface` / `border-border` / `text-fg` / `text-muted` / `text-subtle` / `text-primary` / `text-error`（及 soft 底色 `bg-primary-soft` 等），数据值加 `font-mono`；**禁止 `#hex` 魔法值与内联 style**（echarts option 内的数据系列色与 `design-system/theme/tokens.ts` 品牌常量除外；JS 侧取色用 `useCssTheme()`）。className 组合用 `@/utils/cn`。
3. **删除/批量操作必须用 `Popconfirm` 二次确认**（`title=t('common.confirmDelete')`，okText/cancelText 走 i18n）；`Modal`/`Drawer`/浮层遵循 Radix 默认交互——**点击遮罩或外部区域即可关闭**（Esc 亦可），禁止用 `onInteractOutside` 拦截。
4. **所有文案走 i18n**（`useTranslation()`），`zh-CN` 与 `en-US` 两份语言文件必须同步编辑，禁止出现只在单侧存在的 key。
5. **API 层**：所有请求走 `@/api` 下的封装函数（基于 `client.ts` 的 fetch 包装），禁止在组件里裸写 `fetch/axios`；`token` 由 client 统一注入，401 自动清除登录态。
6. **实体表必须展示审计列**（创建/更新时间 + 操作人），用 `AuditCell` 组件（时间在上、人在下）。
7. 操作按钮统一「图标 + 文字」的 `<Button variant="link" size="sm">`，禁止纯图标按钮；破坏性操作标 `danger`。
8. 页面布局：**页面不渲染标题文案**（页面名由侧边导航承载，`Page` 组件只有可选 `extra` 工具条）、不设置 maxWidth 居中；工具条左对齐；时间显示统一 `formatDateTime()`。
9. **新页面必须在同级 `__tests__/` 建测试**（至少渲染冒烟），`npm run test` 全绿才能合并。注意 v2 组件基于 Radix：Tabs/DropdownMenu/Select 在 jsdom 下分别以 `mousedown`/`pointerDown` 触发（真实浏览器 click 序列自然包含）。
10. `src/api/client.ts` 与 `src/api/types.ts` 是契约层：后端接口变更时先改 `types.ts` 再改封装函数，保持与 OpenAPI 同步。
11. **权限**：角色三值 `admin`/`write`/`readonly`（`@/store/auth` 的 `isAdminRole`/`canWriteRole`）；只读用户必须隐藏写操作按钮（增删改/保存），路由级限制用 `<RequireAuth roles={['admin']}>`；后端授权仍是权威（前端 gating 只是体验层）。
12. **表单**：react-hook-form + `Field`（label/required/error 直传 `errors.xxx?.message`）；`Select` 值一律 string（数字 String() 往返）；受控组件（Switch/Checkbox/自定义 Select）接 rhf 用 `watch`/`setValue`。

## 拖拽布局例外

- `design-system/DashboardGrid.tsx` 及 react-grid-layout 引擎可生成运行时坐标、尺寸与 transform 定位样式。此例外仅用于画布几何布局；页面颜色、控件和装饰仍须使用语义 token，业务页面不得任意添加内联样式。

## 主题系统

- **亮/暗双主题，默认暗色**（不跟随系统偏好）；持久化在 `store/app.ts`（localStorage `sdata-web-theme`，旧 8 套预设 key 自动归一到亮/暗家族），侧边栏底部 Sun/Moon 分段切换。
- 变量组定义在 `src/index.css`：`:root[data-theme='dark'|'light']` 两套 CSS 变量（`--c-bg`/`--c-surface`/`--c-fg`/`--c-primary` 等）+ `@theme inline` 映射为 Tailwind 语义色。**新增全局样式一律走这套变量**，不要另起炉灶。
- 主题应用（`<html data-theme>`）由 `store/app.ts` 在状态变更时**同步**写入（主题桥），`useCssTheme()`（render 期读 CSS 变量）因此无错拍；echarts 等 JS 消费方经 `useCssTheme()` 取色自动跟随。
- 侧边导航是自绘按钮（`layouts/components/AppSider.tsx`，Tailwind class 实现）：非选中项不得写内联 background/color（会盖掉 hover 类）。

## 结构约定

```
src/
├── api/            client.ts（fetch 封装）+ types.ts（DTO 契约）+ 按域封装
├── design-system/  index.tsx（v2 唯一出口）+ components/（Button/Input/Select/Modal/Drawer/Popconfirm/Popover/
│                   Toast/Table/Field/TimeRangePicker/Tabs/DropdownMenu/Tooltip/Tag/Checkbox/Switch/feedback/layout）
│                   + Chart.tsx（echarts 封装）+ theme/（tokens 品牌常量 + useCssTheme + 亮暗调色板）
├── layouts/        AppLayout + components/AppSider|AppLogo（248px 侧边栏，自绘导航）
├── pages/<domain>/ 页面组件 + __tests__/
├── providers/      AppProviders（i18n 同步 + Toaster 挂载）
├── router/         路由表 + RequireAuth（登录 + 可选角色守卫）
├── store/          zustand：auth（登录态，持久化）/ app（主题与语言，持久化 + 同步主题桥）
├── i18n/           zh-CN / en-US 语言包（编辑必须成对）
└── utils/          format.ts（时间/字节/耗时格式化）+ cn.ts（className 组合）
```

## 组件约定速查

- **Toast**：`toast.success/error/info/warning('文案')`（模块级函数，无需 hook）；渲染层 `<Toaster/>` 已挂 AppProviders。
- **Modal**：`<Modal open onOpenChange title footer>`；确认弹窗用 `ConfirmModal`（内部管理 pending）。
- **DataTable**：列 API 沿用 `title/dataIndex/render/key/width/align/ellipsis` 形状并支持 `mono`（等宽数据列）；分页三态——不传=客户端分页（每页 10）、`false`=关、对象带 `onChange`=服务端受控。
- **TimeRangePicker**：HyperDX 式预设+自定义区间受控组件（`TimeRange{from,to,preset?}`）。
- **Select**：`options/groups` + `onValueChange`；浮层与触发器同宽；`testId` 透传 data-testid。
- **Popover** vs **Popconfirm**：前者是非确认型点击浮层（表结构面板等），后者专用于二次确认。

## 构建

- `npm run dev`：4100 端口，`/api` 与 `/hub` 代理到宿主 5300。
- `npm run build`：`tsc -b` + `vite build`（Tailwind 经 `@tailwindcss/vite` 插件，无 postcss 配置），产物输出 `../src/SData/wwwroot`（宿主同源托管，无需独立前端容器）。
- 图表用 `design-system/Chart`（echarts 封装，自动 resize/dispose，主题色随 CSS 变量）；option 中的字面量（`trigger: 'axis'` 等）需要 `as const`。
- pnpm ≥10 的依赖构建白名单在 `pnpm-workspace.yaml`（`onlyBuiltDependencies: [esbuild]`），package.json 的 `pnpm` 字段已废弃勿再加。
