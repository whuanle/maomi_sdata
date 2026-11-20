import type { DashboardConfig, DashboardItem, PanelConfig } from '@/api/types';
import { normalizeDashboard } from './dashboardLayout';

/** 仪表盘/面板导入导出：包装 JSON 格式与解析，导入复用保存链路的深度校验. */
export const DASHBOARD_EXPORT_KIND = 'sdata-dashboard';
export const PANEL_EXPORT_KIND = 'sdata-panel';
const EXPORT_VERSION = 1;

/** 触发浏览器下载 JSON 文件（UTF-8，pretty 缩进便于人工查看）. */
export function downloadJson(fileName: string, text: string): void {
  const blob = new Blob([text], { type: 'application/json;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

/** 文件名安全化：替换路径与保留字符，空名回退默认值. */
export function safeFileName(name: string, fallback: string): string {
  const cleaned = name.replace(/[/\\:*?"<>|]/g, '-').trim().slice(0, 64);
  return `${cleaned || fallback}.json`;
}

/** 整盘导出 JSON（导出归一化后的配置，WYSIWYG）. */
export function buildDashboardExport(item: Pick<DashboardItem, 'name' | 'description'>, config: DashboardConfig): string {
  return JSON.stringify({
    kind: DASHBOARD_EXPORT_KIND,
    version: EXPORT_VERSION,
    exportedAt: new Date().toISOString(),
    name: item.name,
    description: item.description,
    dashboard: config,
  }, null, 2);
}

/** 单面板导出 JSON（分组导出时包含全部子面板；layout 仅保留尺寸）. */
export function buildPanelExport(panel: PanelConfig): string {
  return JSON.stringify({
    kind: PANEL_EXPORT_KIND,
    version: EXPORT_VERSION,
    exportedAt: new Date().toISOString(),
    title: panel.title,
    panel,
  }, null, 2);
}

/** 解析整盘导入 JSON：校验包装格式后走 normalizeDashboard 做版本迁移与结构校验. */
export function parseDashboardImport(text: string): { name: string; description: string; config: DashboardConfig } {
  let payload: { kind?: unknown; version?: unknown; name?: unknown; description?: unknown; dashboard?: unknown };
  try {
    payload = JSON.parse(text) as typeof payload;
  } catch {
    throw new Error('dashboard.importInvalid');
  }
  if (payload?.kind !== DASHBOARD_EXPORT_KIND || payload.version !== EXPORT_VERSION || !payload.dashboard || typeof payload.dashboard !== 'object') {
    throw new Error('dashboard.importInvalid');
  }
  return {
    name: typeof payload.name === 'string' ? payload.name : '',
    description: typeof payload.description === 'string' ? payload.description : '',
    config: normalizeDashboard(JSON.stringify(payload.dashboard)),
  };
}

/** 重生成面板及分组子面板 id，避免与目标仪表盘冲突；分组默认标签同步重映射. */
function regeneratePanelIds(panel: PanelConfig): PanelConfig {
  if (panel.type !== 'group') return { ...structuredClone(panel), id: crypto.randomUUID() };
  const source = panel.panels ?? [];
  const panels = source.map((child) => regeneratePanelIds(child));
  const remap = new Map(source.map((child, index) => [child.id, panels[index].id]));
  const defaultPanelId = panel.defaultPanelId ? remap.get(panel.defaultPanelId) ?? panels[0]?.id : undefined;
  return { ...structuredClone(panel), id: crypto.randomUUID(), panels, defaultPanelId };
}

/** 解析面板导入 JSON：结构经 normalizeDashboard 校验并归一布局，id 重生成避免冲突. */
export function parsePanelImport(text: string): PanelConfig {
  let payload: { kind?: unknown; version?: unknown; panel?: unknown };
  try {
    payload = JSON.parse(text) as typeof payload;
  } catch {
    throw new Error('dashboard.importInvalid');
  }
  if (payload?.kind !== PANEL_EXPORT_KIND || payload.version !== EXPORT_VERSION || !payload.panel || typeof payload.panel !== 'object') {
    throw new Error('dashboard.importInvalid');
  }
  const panel = payload.panel as PanelConfig;
  const version = panel.type === 'group' ? 3 : 2;
  const normalized = normalizeDashboard(JSON.stringify({ version, panels: [panel] }));
  return regeneratePanelIds(normalized.panels[0]);
}
