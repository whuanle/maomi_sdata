import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import '@/i18n';
import { CustomNodeEditor } from '../CustomNodeEditor';
import { DesignerFieldHints } from '../DesignerFieldHints';
import { newCustomNode } from '../customPanel';
import type { CustomPanelNode } from '@/api/types';

vi.mock('@/pages/search/SqlEditor', () => ({
  SqlEditor: ({ value, readOnly, onChange }: { value: string; readOnly?: boolean; onChange?: (sql: string) => void }) =>
    <textarea aria-label={readOnly ? 'Generated SQL' : 'SQL'} readOnly={readOnly} value={value} onChange={event => onChange?.(event.target.value)} />,
}));
const range = { from: '2026-11-19T00:00:00Z', to: '2026-11-19T01:00:00Z' };

function Editor({ initial = newCustomNode(0) }: { initial?: CustomPanelNode }) {
  const [node, setNode] = useState(initial);
  return <CustomNodeEditor node={node} preceding={[]} range={range} columns={[]}
    onChange={patch => setNode(previous => ({ ...previous, ...patch }))} onRun={vi.fn()} onInvalid={vi.fn()} />;
}

it('gives hints bounded inline controls that can wrap on narrow screens', () => {
  render(<DesignerFieldHints range={range} source="logs" />);
  fireEvent.click(screen.getByText('字段与属性提示'));
  const source = screen.getByRole('combobox', { name: '数据来源' });
  const root = screen.getByRole('combobox', { name: 'JSON 字段' });
  const load = screen.getByRole('button', { name: '加载属性提示' });
  expect(source).toHaveClass('w-40', 'shrink-0');
  expect(root).toHaveClass('w-40', 'shrink-0');
  expect(load).toHaveClass('shrink-0');
  expect(source.parentElement).toBe(root.parentElement);
  expect(root.parentElement).toBe(load.parentElement);
  expect(load.parentElement).toHaveClass('flex', 'flex-wrap');
  expect(source).toBeDisabled();
});

it('defaults to visual controls and requires confirmation to rebuild handwritten SQL', () => {
  render(<Editor />);
  expect(screen.getByRole('combobox', { name: 'Lucene 筛选 A' })).toBeInTheDocument();
  expect(screen.queryByRole('textbox', { name: '绑定参数（JSON） A' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '高级编辑' }));
  const sql = screen.getByRole('textbox', { name: 'SQL' });
  fireEvent.change(sql, { target: { value: 'SELECT 99 AS value' } });
  fireEvent.click(screen.getByRole('button', { name: '使用可视化配置' }));
  expect(screen.getByRole('dialog')).toBeInTheDocument();
  expect(sql).toHaveValue('SELECT 99 AS value');
  fireEvent.click(screen.getByRole('button', { name: '取消' }));
  expect(sql).toHaveValue('SELECT 99 AS value');
});

it('keeps existing SQL in advanced mode without inferring a builder', () => {
  const initial = newCustomNode(0);
  initial.editor = undefined;
  initial.sql = 'SELECT $input AS value';
  render(<Editor initial={initial} />);
  expect(screen.getByRole('textbox', { name: 'SQL' })).toHaveValue(initial.sql);
  expect(screen.queryByRole('combobox', { name: 'Lucene 筛选 A' })).not.toBeInTheDocument();
});
