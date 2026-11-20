import type { DashboardConfig, PanelConfig, PanelLayout } from '@/api/types';

export const GRID_ROW_HEIGHT = 96;
export const GRID_GAP = 12;

export function emptyDashboard(): DashboardConfig {
  return { version: 2, time: { preset: '1h' }, refreshSeconds: 0, panels: [] };
}

function overlaps(a: PanelLayout, b: PanelLayout): boolean {
  return (a.x ?? 0) < (b.x ?? 0) + b.w && (a.x ?? 0) + a.w > (b.x ?? 0)
    && (a.y ?? 0) < (b.y ?? 0) + b.h && (a.y ?? 0) + a.h > (b.y ?? 0);
}

/** 保留扩展字段；旧 w/h 按顺序迁移为不重叠坐标，不触碰存储数据. */
export function normalizeDashboard(configJson?: string): DashboardConfig {
  const raw = JSON.parse(configJson || '{}') as Partial<DashboardConfig>;
  if (raw.version !== undefined && raw.version !== 1 && raw.version !== 2 && raw.version !== 3) {
    throw new Error('dashboard.unsupportedVersion');
  }
  if (!raw || typeof raw !== 'object' || (raw.panels !== undefined && !Array.isArray(raw.panels))) throw new Error('dashboard.invalidConfig');
  const placed: PanelConfig[] = [];
  const ids = new Set<string>();
  for (const panel of raw.panels ?? []) {
    if (!panel || !panel.id || ids.has(panel.id)) throw new Error('dashboard.invalidConfig');
    ids.add(panel.id);
    if (panel.type === 'group') {
      if (raw.version !== 3 || !Array.isArray(panel.panels) || panel.panels.some(child => !child?.id || child.type === 'group' || child.panels != null || ids.has(child.id))) throw new Error('dashboard.invalidConfig');
      for (const child of panel.panels) {
        if (ids.has(child.id)) throw new Error('dashboard.invalidConfig');
        ids.add(child.id);
      }
      if (panel.defaultPanelId && !panel.panels.some(child => child.id === panel.defaultPanelId)) throw new Error('dashboard.invalidConfig');
    } else if (panel.panels != null || panel.defaultPanelId != null) throw new Error('dashboard.invalidConfig');
    if (ids.size > 100) throw new Error('dashboard.invalidConfig');
    const w = Math.max((raw.version ?? 1) >= 2 ? 1 : 2, Math.min(12, panel.layout?.w ?? 6));
    const oldH = panel.layout?.h ?? 4;
    const h = Math.max((raw.version ?? 1) >= 2 ? 1 : 2, Math.min(8, (raw.version ?? 1) >= 2 ? oldH : Math.round((oldH * 100 + 32) / 108)));
    const layout: PanelLayout = { w, h, x: 0, y: 0 };
    if ((raw.version ?? 1) >= 2 && Number.isInteger(panel.layout?.x) && Number.isInteger(panel.layout?.y)) {
      layout.x = Math.max(0, Math.min(12 - w, panel.layout!.x!));
      layout.y = Math.max(0, panel.layout!.y!);
    } else {
      let found = false;
      for (let y = 0; !found; y++) {
        for (let x = 0; x <= 12 - w; x++) {
          const candidate = { ...layout, x, y };
          if (!placed.some((item) => overlaps(candidate, item.layout!))) {
            Object.assign(layout, candidate);
            found = true;
            break;
          }
        }
      }
    }
    while (placed.some((item) => overlaps(layout, item.layout!))) layout.y!++;
    placed.push({ ...panel, layout });
  }
  return { ...emptyDashboard(), ...raw, version: raw.version === 3 ? 3 : 2, panels: placed };
}

export function appendPanel(config: DashboardConfig, panel: PanelConfig): DashboardConfig {
  const bottom = Math.max(0, ...config.panels.map((item) => (item.layout?.y ?? 0) + (item.layout?.h ?? 4)));
  return { ...config, panels: [...config.panels, { ...panel, layout: { w: panel.layout?.w ?? 6, h: panel.layout?.h ?? 4, x: 0, y: bottom } }] };
}
