import type { DashboardConfig, PanelConfig } from '@/api/types';
import { appendPanel } from './dashboardLayout';

export function leafPanels(panels: PanelConfig[]): PanelConfig[] {
  return panels.flatMap(panel => panel.type === 'group' ? panel.panels ?? [] : [panel]);
}

export function findPanel(config: DashboardConfig, id: string): PanelConfig | undefined {
  return config.panels.find(panel => panel.id === id) ?? leafPanels(config.panels).find(panel => panel.id === id);
}

export function replacePanel(config: DashboardConfig, panel: PanelConfig): DashboardConfig {
  return { ...config, panels: config.panels.map(item => item.id === panel.id ? panel
    : item.type === 'group' ? { ...item, panels: item.panels?.map(child => child.id === panel.id ? { ...panel, layout: child.layout } : child) } : item) };
}

export function removePanel(config: DashboardConfig, id: string): DashboardConfig {
  return { ...config, panels: config.panels.filter(panel => panel.id !== id).map(panel => {
    if (panel.type !== 'group') return panel;
    const panels = panel.panels?.filter(child => child.id !== id) ?? [];
    return { ...panel, panels, defaultPanelId: panels.some(child => child.id === panel.defaultPanelId) ? panel.defaultPanelId : panels[0]?.id };
  }) };
}

export function clonePanel(panel: PanelConfig, suffix: string): PanelConfig {
  const panels = panel.panels?.map(child => clonePanel(child, suffix));
  return { ...structuredClone(panel), id: crypto.randomUUID(), title: `${panel.title} · ${suffix}`, panels,
    defaultPanelId: panels?.[Math.max(0, panel.panels?.findIndex(child => child.id === panel.defaultPanelId) ?? 0)]?.id };
}

/** 管理分组时只移动成员，不丢弃查询定义；移出的组件追加到画布底部. */
export function applyGroup(config: DashboardConfig, group: PanelConfig): DashboardConfig {
  const members = group.panels ?? [];
  group = { ...group, panels: members, defaultPanelId: members.some(panel => panel.id === group.defaultPanelId) ? group.defaultPanelId : members[0]?.id };
  const selected = new Set(members.map(panel => panel.id));
  const previous = config.panels.find(panel => panel.id === group.id);
  let next = { ...config, version: 3, panels: config.panels.filter(panel => !selected.has(panel.id)).map(panel => panel.id === group.id ? group : panel) };
  if (!previous) next = appendPanel(next, group);
  for (const child of previous?.panels ?? []) {
    if (!selected.has(child.id)) next = appendPanel(next, child);
  }
  return next;
}

export function movePanelIntoGroup(config: DashboardConfig, panelId: string, groupId: string): DashboardConfig {
  const panel = config.panels.find(item => item.id === panelId);
  const group = config.panels.find(item => item.id === groupId && item.type === 'group');
  if (!panel || panel.type === 'group' || !group) return config;
  return applyGroup(config, { ...group, panels: [...(group.panels ?? []), panel], defaultPanelId: group.defaultPanelId ?? panel.id });
}

export function dissolveGroup(config: DashboardConfig, group: PanelConfig): DashboardConfig {
  return (group.panels ?? []).reduce(appendPanel, { ...config, panels: config.panels.filter(panel => panel.id !== group.id) });
}
