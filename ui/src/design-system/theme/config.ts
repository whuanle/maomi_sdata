import { themePalettes, type ThemeKey, type ThemeMode, type ThemePalette } from './presets'

export type { ThemeKey, ThemeMode }

export interface ThemePreset {
  key: ThemeKey
  /** 展示名 i18n key（common.dark/common.light，zh-CN 与 en-US 同步维护） */
  nameKey: string
  /** 中文兜底名（非 React 场景用，页面展示一律走 nameKey） */
  name: string
  mode: ThemeMode
  palette: ThemePalette
}

/** v2 默认暗色（HyperDX 式观测台）. */
export const defaultThemeKey: ThemeKey = 'dark'

export const defaultThemeKeyByMode: Record<ThemeMode, ThemeKey> = {
  light: 'light',
  dark: 'dark',
}

const themeNameKeys: Record<ThemeKey, string> = {
  dark: 'common.dark',
  light: 'common.light',
}

const themeNames: Record<ThemeKey, string> = {
  dark: '暗色',
  light: '亮色',
}

const themeKeyOrder: ThemeKey[] = ['dark', 'light']

/** 全部预设（暗色在前，默认项），供展示用 */
export const themePresetList: ThemePreset[] = themeKeyOrder.map((key) => ({
  key,
  nameKey: themeNameKeys[key],
  name: themeNames[key],
  mode: themePalettes[key].mode,
  palette: themePalettes[key],
}))

export const themePresets: Record<ThemeKey, ThemePreset> = Object.fromEntries(
  themePresetList.map((preset) => [preset.key, preset]),
) as Record<ThemeKey, ThemePreset>

export function isThemeKey(value: unknown): value is ThemeKey {
  return value === 'dark' || value === 'light'
}

/**
 * 旧版 8 套预设 key → 亮/暗归一（store 迁移旧 localStorage 值用）.
 * 亮色家族：light/paper/sakura/green；暗色家族：dark/midnight/forest/violet.
 */
export function legacyThemeKeyToMode(value: string): ThemeKey | undefined {
  return ['light', 'paper', 'sakura', 'green'].includes(value)
    ? 'light'
    : ['dark', 'midnight', 'forest', 'violet'].includes(value)
      ? 'dark'
      : undefined
}

export function getThemePreset(themeKey: ThemeKey): ThemePreset {
  return themePresets[themeKey]
}
