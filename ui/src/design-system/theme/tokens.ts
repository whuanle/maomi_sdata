/**
 * 品牌常量（素净原则，对齐 HyperDX ClickStack 观感）：
 * 纯中性灰底面、低饱和紫强调、无渐变、图表色板取 HyperDX 分类色。
 * 注意：与 index.css 的 CSS 变量组保持一致（两处取值须同步修改）。
 */

export const colorPrimary = '#7A6FE0'

export const brandColors = {
  primary: colorPrimary,
  primaryHover: '#8F86E8',
  primaryActive: '#6B61CF',
  success: '#22C55E',
  warning: '#F59E0B',
  error: '#EF4444',
  info: '#437EEF',
}

/** 图表/分类回退色板（echarts 系列色共用，避免页面各存一份漂移）；取 HyperDX 分类 10 色. */
export const chartColors = ['#437eef', '#efb118', '#ff725c', '#6cc5b0', '#3ca951', '#ff8ab7', '#a463f2', '#97bbf5', '#9c6b4e', '#9498a0']

export const neutralColors = {
  textPrimary: '#1B1B1E',
  textSecondary: '#57575E',
  textTertiary: '#8B8B92',
  border: '#E4E4E6',
  background: '#F6F6F7',
  backgroundElevated: '#FFFFFF',
}

/** 小圆角（HyperDX 式方正观感） */
export const radius = {
  sm: 4,
  default: 5,
  lg: 8,
  pill: 999,
}

export const spacing = {
  xxs: 4,
  xs: 8,
  sm: 12,
  md: 16,
  lg: 24,
  xl: 32,
  xxl: 48,
}

/** 字体对齐 HyperDX：只声明 Inter，未安装时直接落 Helvetica/Arial（不走 Segoe UI 等圆润回退链）. */
export const fontFamily = "Inter, 'Helvetica Neue', Arial, sans-serif"

export const fontSize = {
  xs: 12,
  sm: 13,
  md: 14,
  lg: 16,
  xl: 20,
  xxl: 24,
}

export const controlHeight = 36

/** 图表探索页强调色（HyperDX 风格绿：运行按钮/激活页签描边）. */
export const accentGreen = '#0E8A5F'

/** 图表探索页等宽字体（HyperDX 式全等宽界面）. */
export const monoFontFamily = "'JetBrains Mono', 'Cascadia Mono', Consolas, Menlo, monospace"
