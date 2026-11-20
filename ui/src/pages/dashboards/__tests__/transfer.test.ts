import { describe, expect, it } from 'vitest';
import type { PanelConfig } from '@/api/types';
import { appendPanel } from '../dashboardLayout';
import {
  buildDashboardExport,
  buildPanelExport,
  DASHBOARD_EXPORT_KIND,
  PANEL_EXPORT_KIND,
  parseDashboardImport,
  parsePanelImport,
  safeFileName,
} from '../transfer';

const linePanel: PanelConfig = {
  id: 'p1', title: 'CPU 使用率', type: 'line', layout: { w: 6, h: 4, x: 3, y: 2 },
  query: { kind: 'metric', name: 'cpu.usage', aggregation: 'avg' },
};

describe('整盘导出与导入', () => {
  it('导出 JSON 携带包装格式并可解析还原', () => {
    const text = buildDashboardExport({ name: '验证盘', description: '说明' }, { version: 2, time: { preset: '1h' }, refreshSeconds: 0, panels: [linePanel] });
    const parsed = JSON.parse(text);
    expect(parsed.kind).toBe(DASHBOARD_EXPORT_KIND);
    expect(parsed.version).toBe(1);
    expect(parsed.name).toBe('验证盘');
    expect(parsed.description).toBe('说明');
    expect(parseDashboardImport(text)).toEqual({
      name: '验证盘', description: '说明',
      config: { version: 2, time: { preset: '1h' }, refreshSeconds: 0, panels: [{ ...linePanel, layout: { w: 6, h: 4, x: 3, y: 2 } }] },
    });
  });

  it('非法 JSON、错误 kind 或缺失 dashboard 被拒绝', () => {
    expect(() => parseDashboardImport('{oops')).toThrow('dashboard.importInvalid');
    expect(() => parseDashboardImport(JSON.stringify({ kind: 'other', version: 1, dashboard: {} }))).toThrow('dashboard.importInvalid');
    expect(() => parseDashboardImport(JSON.stringify({ kind: DASHBOARD_EXPORT_KIND, version: 1 }))).toThrow('dashboard.importInvalid');
  });

  it('内部仪表盘版本不受支持时透传 normalize 错误', () => {
    const text = JSON.stringify({ kind: DASHBOARD_EXPORT_KIND, version: 1, dashboard: { version: 99, panels: [] } });
    expect(() => parseDashboardImport(text)).toThrow('dashboard.unsupportedVersion');
  });

  it('重复面板 id 被拒绝', () => {
    const text = JSON.stringify({ kind: DASHBOARD_EXPORT_KIND, version: 1, dashboard: { version: 2, panels: [linePanel, linePanel] } });
    expect(() => parseDashboardImport(text)).toThrow('dashboard.invalidConfig');
  });
});

describe('面板导出与导入', () => {
  it('普通面板导入保留标题与尺寸并重生成 id', () => {
    const text = buildPanelExport(linePanel);
    const panel = parsePanelImport(text);
    expect(panel.title).toBe('CPU 使用率');
    expect(panel.id).not.toBe(linePanel.id);
    expect(panel.layout?.w).toBe(6);
    expect(panel.layout?.h).toBe(4);
  });

  it('分组面板导入重映射子面板 id 与默认标签', () => {
    const group: PanelConfig = {
      id: 'g1', title: '资源组', type: 'group', layout: { w: 12, h: 4 },
      panels: [linePanel, { ...linePanel, id: 'p2', title: '内存' }], defaultPanelId: 'p2',
    };
    const panel = parsePanelImport(buildPanelExport(group));
    expect(panel.type).toBe('group');
    expect(panel.panels).toHaveLength(2);
    expect(panel.panels!.map((child) => child.id)).not.toContain('p1');
    expect(panel.panels!.map((child) => child.id)).not.toContain('p2');
    expect(panel.defaultPanelId).toBe(panel.panels!.find((child) => child.title === '内存')!.id);
  });

  it('结构非法的面板被拒绝且导入后可追加落位', () => {
    const text = JSON.stringify({ kind: PANEL_EXPORT_KIND, version: 1, panel: { title: '无 id', type: 'line' } });
    expect(() => parsePanelImport(text)).toThrow('dashboard.invalidConfig');
    const panel = parsePanelImport(buildPanelExport(linePanel));
    const config = appendPanel({ version: 2, time: { preset: '1h' }, refreshSeconds: 0, panels: [] }, panel);
    expect(config.panels[0].layout).toEqual({ w: 6, h: 4, x: 0, y: 0 });
  });

  it('错误 kind 或缺失 panel 被拒绝', () => {
    expect(() => parsePanelImport('not-json')).toThrow('dashboard.importInvalid');
    expect(() => parsePanelImport(JSON.stringify({ kind: PANEL_EXPORT_KIND, version: 1 }))).toThrow('dashboard.importInvalid');
  });
});

describe('文件名安全化', () => {
  it('替换非法字符并追加 .json 后缀', () => {
    expect(safeFileName('a/b\\c:d*e?f"g<h>i|j', 'dashboard')).toBe('a-b-c-d-e-f-g-h-i-j.json');
    expect(safeFileName('  ', 'dashboard')).toBe('dashboard.json');
  });
});
