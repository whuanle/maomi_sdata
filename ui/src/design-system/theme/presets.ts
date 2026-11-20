/**
 * 主题预设调色板（v2：亮/暗双主题，默认暗色）。
 * 调色板同时服务两个消费者：
 * 1. index.css 的 CSS 变量组（组件层 Tailwind 语义色的数据源，两处取值须保持一致）；
 * 2. 过渡期 antd ThemeConfig 生成（未迁移页面仍在用，清理 antd 时随 config.ts 一并删除）。
 */

export type ThemeMode = 'light' | 'dark'

/** v2 主题 key 与亮暗模式合一（不再有 8 套预设）. */
export type ThemeKey = ThemeMode

export interface ThemePalette {
  mode: ThemeMode
  /** 强调色（主按钮/链接/选中态） */
  accent: string
  /** 页面底色（body 与布局底） */
  bgLayout: string
  /** 卡片与输入类控件容器色 */
  bgContainer: string
  /** 弹层/下拉等浮层底色 */
  bgElevated: string
  textPrimary: string
  textSecondary: string
  textTertiary: string
  textQuaternary: string
  border: string
  borderSecondary: string
  /** 侧边导航 */
  menu: {
    item: string
    hover: string
    hoverBg: string
    selected: string
    selectedBg: string
  }
  /** 表格头 */
  table: {
    headerBg: string
    headerColor: string
    borderColor: string
  }
}

const darkPalette: ThemePalette = {
  mode: 'dark',
  accent: '#7A6FE0',
  bgLayout: '#1C1C1E',
  bgContainer: '#262628',
  bgElevated: '#2E2E30',
  textPrimary: '#F2F2F2',
  textSecondary: '#B3B6BD',
  textTertiary: '#85868A',
  textQuaternary: '#626266',
  border: '#333336',
  borderSecondary: '#2E2E30',
  menu: {
    item: '#B3B6BD',
    hover: '#F2F2F2',
    hoverBg: '#2E2E30',
    selected: '#8F86E8',
    selectedBg: 'rgba(122, 111, 224, 0.14)',
  },
  table: { headerBg: '#2E2E30', headerColor: '#B3B6BD', borderColor: '#333336' },
}

const lightPalette: ThemePalette = {
  mode: 'light',
  accent: '#6459CE',
  bgLayout: '#F6F6F7',
  bgContainer: '#FFFFFF',
  bgElevated: '#FFFFFF',
  textPrimary: '#1B1B1E',
  textSecondary: '#57575E',
  textTertiary: '#8B8B92',
  textQuaternary: '#BCBCC2',
  border: '#E4E4E6',
  borderSecondary: '#F0F0F1',
  menu: {
    item: '#57575E',
    hover: '#1B1B1E',
    hoverBg: '#F0F0F1',
    selected: '#5549C6',
    selectedBg: '#EEEDF8',
  },
  table: { headerBg: '#FAFAFA', headerColor: '#737378', borderColor: '#F0F0F1' },
}

export const themePalettes: Record<ThemeKey, ThemePalette> = {
  dark: darkPalette,
  light: lightPalette,
}
