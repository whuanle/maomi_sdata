import { create } from 'zustand';
import { defaultThemeKeyByMode, legacyThemeKeyToMode, themePresets, type ThemeKey } from '@/design-system/theme';

export type Locale = 'zh-CN' | 'en-US';

interface AppState {
  themeKey: ThemeKey;
  locale: Locale;
  setThemeKey: (key: ThemeKey) => void;
  toggleTheme: () => void;
  setLocale: (locale: Locale) => void;
}

/**
 * 主题桥（同步）：store 一旦确定主题立即落到 <html data-theme>，供
 * index.css 变量组与 useCssTheme（render 期读 CSS 变量）使用——
 * 同步应用保证切主题后首个渲染读到的就是新值，不存在错拍。
 */
function applyTheme(key: ThemeKey) {
  if (typeof document === 'undefined') return;
  document.documentElement.dataset.theme = key;
  document.documentElement.dataset.themeMode = key;
  document.documentElement.style.setProperty('--sdata-bg-layout', themePresets[key].palette.bgLayout);
}

const getInitialTheme = (): ThemeKey => {
  const saved = localStorage.getItem('sdata-web-theme');
  let themeKey: ThemeKey;
  if (saved === 'dark' || saved === 'light') {
    themeKey = saved;
  } else if (saved) {
    // 旧版 8 套预设 → 亮/暗家族归一
    const legacy = legacyThemeKeyToMode(saved);
    themeKey = legacy ?? defaultThemeKeyByMode.dark;
  } else {
    // v2 默认暗色（HyperDX 式观测台），不跟随系统偏好
    themeKey = defaultThemeKeyByMode.dark;
  }
  applyTheme(themeKey);
  return themeKey;
};

const getInitialLocale = (): Locale => {
  const saved = localStorage.getItem('sdata-web-locale');
  if (saved === 'zh-CN' || saved === 'en-US') return saved;
  return 'zh-CN';
};

/** 应用外观 store：亮/暗主题 + 语言（localStorage 持久化）. */
export const useAppStore = create<AppState>()((set, get) => ({
  themeKey: getInitialTheme(),
  locale: getInitialLocale(),

  setThemeKey: (key) => {
    applyTheme(key);
    localStorage.setItem('sdata-web-theme', key);
    set({ themeKey: key });
  },
  toggleTheme: () => {
    const currentMode = themePresets[get().themeKey].mode;
    const next = defaultThemeKeyByMode[currentMode === 'dark' ? 'light' : 'dark'];
    applyTheme(next);
    localStorage.setItem('sdata-web-theme', next);
    set({ themeKey: next });
  },
  setLocale: (locale) => {
    localStorage.setItem('sdata-web-locale', locale);
    set({ locale });
  },
}));
