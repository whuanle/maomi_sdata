import type { CustomPanelNode, PanelConfig } from '@/api/types';
import { parseFormula, seriesRef, type FormulaNode } from '@/pages/charts/formula';
import { defaultNodeEditor } from './customNodeBuilder';

export function newCustomNode(index: number, kind: CustomPanelNode['kind'] = 'sql'): CustomPanelNode {
  return { ref: seriesRef(index), kind, editor: kind === 'sql' ? defaultNodeEditor() : { ...defaultNodeEditor(), mode: 'guided', operation: 'ratio', left: 'A', right: index > 1 ? 'B' : 'A' }, sql: 'SELECT count(*) AS value FROM {{logs}} WHERE ts >= $start AND ts < $end', parameters: {}, shape: 'scalar', valueField: 'value', timeField: 'ts', groupFields: [], expression: index ? seriesRef(index - 1) : '1', reduce: '' };
}
export function newCustomPanel(): PanelConfig {
  return { id: crypto.randomUUID(), title: '', type: 'stat', layout: { w: 6, h: 4 }, visualization: { version: 1, kind: 'number', display: 'line', range: { from: new Date(Date.now() - 3600000).toISOString(), to: new Date().toISOString() }, outputs: [{ id: 'A', alias: 'A' }] }, query: { kind: 'custom', custom: { version: 1, nodes: [newCustomNode(0)], outputs: ['A'] } } };
}
function references(node: FormulaNode): string[] {
  if (node.kind === 'ref') return [node.value];
  if (node.kind === 'unary') return references(node.child);
  if (node.kind === 'binary') return [...references(node.left), ...references(node.right)];
  return [];
}
export function validateCustomPanel(panel: PanelConfig): string | null {
  const definition = panel.query?.custom;
  if (!definition || definition.version !== 1 || !Array.isArray(definition.nodes) || !Array.isArray(definition.outputs)) return 'designer.unsupported';
  if (!panel.title.trim()) return 'dashboard.panelTitleRequired';
  if (panel.title.length > 128 || !definition.nodes.length || definition.nodes.length > 20 || !definition.nodes.some(n => n.kind === 'sql') || definition.nodes.filter(n => n.kind === 'sql').length > 12 || definition.nodes.filter(n => n.kind === 'expression').length > 8) return 'designer.invalid';
  const shapes = new Map<string, CustomPanelNode['shape']>();
  for (const node of definition.nodes) {
    if (!/^[A-Z]{1,3}$/.test(node.ref) || shapes.has(node.ref)) return 'designer.invalid';
    if (node.kind === 'expression') {
      try {
        const ast = parseFormula(node.expression, [...shapes.keys()]);
        const inputs = [...new Set(references(ast).map(ref => shapes.get(ref)!))];
        if (inputs.includes('table')) return 'designer.invalid';
        const vectors = inputs.filter(shape => shape !== 'scalar');
        if (vectors.length > 1) return 'designer.invalid';
        shapes.set(node.ref, node.reduce || !vectors.length ? 'scalar' : vectors[0]);
      } catch { return 'designer.invalidExpression'; }
    } else {
      if (!node.sql.trim() || node.sql.length > 100000 || (node.shape !== 'table' && !node.valueField) || (node.shape === 'series' && !node.timeField)) return 'designer.invalid';
      shapes.set(node.ref, node.shape);
    }
  }
  if (!definition.outputs.length || new Set(definition.outputs).size !== definition.outputs.length || definition.outputs.some(ref => !shapes.has(ref))) return 'designer.invalid';
  const outputs = definition.outputs.map(ref => shapes.get(ref)!);
  if (outputs.includes('table') && outputs.length > 1) return 'designer.invalid';
  if (['line', 'area'].includes(panel.type) && outputs.some(shape => shape !== 'series')) return 'designer.shapeMismatch';
  if (panel.type === 'stat' && outputs.some(shape => shape !== 'scalar')) return 'designer.shapeMismatch';
  if (['bar', 'pie'].includes(panel.type) && outputs.some(shape => !['scalar', 'category'].includes(shape))) return 'designer.shapeMismatch';
  return null;
}
