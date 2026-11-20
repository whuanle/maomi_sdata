import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Code, Plus, RotateCcw } from 'lucide-react';
import { Button, Checkbox, ConfirmModal, Field, Input, Select, Textarea, type TimeRange } from '@/design-system';
import type { CustomNodeEditor as EditorDefinition, CustomPanelNode } from '@/api/types';
import { SEARCH_FIELDS } from '@/api/search';
import { DOMAIN_COLUMNS } from '@/pages/charts/buildChartSql';
import { WhereSuggestInput } from '@/pages/search/WhereSuggestInput';
import { MetricPicker } from '@/pages/charts/MetricPicker';
import { SqlEditor } from '@/pages/search/SqlEditor';
import { DesignerFieldHints } from './DesignerFieldHints';
import { compileCustomNode, defaultNodeEditor } from './customNodeBuilder';

export function CustomNodeEditor({ node, preceding, range, columns, onChange, onRun, onInvalid }: {
  node: CustomPanelNode; preceding: CustomPanelNode[]; range: TimeRange; columns: string[];
  onChange: (patch: Partial<CustomPanelNode>) => void; onRun: () => void; onInvalid: (invalid: boolean) => void;
}) {
  const { t } = useTranslation();
  const [rebuild, setRebuild] = useState(false);
  const editor = node.editor;
  const [compileError, setCompileError] = useState('');
  const patchEditor = (patch: Partial<EditorDefinition>) => {
    const nextEditor = { ...(editor ?? defaultNodeEditor()), ...patch };
    const next = { ...node, editor: nextEditor };
    try {
      const compiled = compileCustomNode(next, range);
      onChange(compiled); setCompileError(''); onInvalid(false);
    } catch (reason) {
      onChange({ editor: nextEditor });
      setCompileError(reason instanceof Error ? reason.message : String(reason)); onInvalid(true);
    }
  };
  const toAdvanced = () => {
    try {
      onChange({ ...compileCustomNode(node, range), editor: undefined }); onInvalid(false); setCompileError('');
    } catch (reason) { setCompileError(String(reason)); }
  };
  if (editor && editor.version !== 1) return <p role="alert" className="text-warning">{t('designer.unsupported')}</p>;
  const source = editor?.source ?? 'logs';
  const fields = SEARCH_FIELDS[source];
  const aggregations = ['count', 'avg', 'sum', 'min', 'max', 'p50', 'p90', 'p95', 'p99', 'count_distinct'] as const;
  const choices = preceding.map(item => ({ value: item.ref, label: item.ref + (item.alias ? ' · ' + item.alias : '') }));
  let generated = node;
  try { generated = compileCustomNode(node, range); } catch { /* 草稿错误由运行和局部反馈展示. */ }
  return <div className="space-y-3">
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-xs text-muted">{t(editor ? node.kind === 'sql' ? 'designer.visualQuery' : 'designer.guidedExpression' : 'designer.advanced')}</span>
      {editor
        ? <Button variant="link" size="sm" icon={<Code className="size-3.5" />} onClick={toAdvanced}>{t('designer.advanced')}</Button>
        : <Button variant="link" size="sm" icon={<RotateCcw className="size-3.5" />} onClick={() => setRebuild(true)}>{t('designer.rebuild')}</Button>}
    </div>
    {compileError && <p role="alert" className="text-xs text-error">{t(compileError)}</p>}
    {node.kind === 'sql' && editor?.mode === 'builder' ? <>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Field label={t('designer.source')}><Select aria-label={t('designer.source') + ' ' + node.ref} value={source}
          options={Object.keys(SEARCH_FIELDS).map(value => ({ value, label: t(value === 'iot' ? 'charts.sourceMqtt' : 'charts.source_' + value, value) }))}
          onValueChange={value => patchEditor({ source: value as EditorDefinition['source'], field: value === 'iot' ? 'num_value' : value === 'metrics' ? 'value' : '', aggregation: value === 'iot' ? 'avg' : 'count', groupBy: [], where: '', metricName: undefined, metricFilters: [] })} /></Field>
        <Field label={t('designer.organize')}><Select aria-label={t('designer.organize') + ' ' + node.ref} value={editor.shape}
          options={['scalar', 'series', 'category', 'table'].map(value => ({ value, label: t('designer.organize_' + value) }))}
          onValueChange={value => patchEditor({ shape: value as EditorDefinition['shape'], groupBy: value === 'category' ? [source === 'iot' ? 'device_key' : source === 'metrics' ? 'name' : source === 'sessions' ? 'domain' : 'service'] : [] })} /></Field>
        {editor.shape !== 'table' && <>
          <Field label={t('designer.aggregation')}><Select aria-label={t('designer.aggregation') + ' ' + node.ref} value={editor.aggregation}
            options={aggregations.filter(value => ['count', 'count_distinct'].includes(value) || fields.some(field => field.kind === 'number')).map(value => ({ value, label: t('designer.agg_' + value) }))}
            onValueChange={value => patchEditor({ aggregation: value as EditorDefinition['aggregation'], field: editor.field || fields.find(field => field.kind === 'number')?.field || fields[0].field })} /></Field>
          {editor.aggregation !== 'count' && <Field label={t('designer.valueField')}><Select aria-label={t('designer.valueField') + ' ' + node.ref} value={editor.field}
            options={fields.filter(field => editor.aggregation === 'count_distinct' || field.kind === 'number').map(field => ({ value: field.field, label: field.field }))}
            onValueChange={field => patchEditor({ field })} /></Field>}
        </>}
        {editor.shape === 'series' && <Field label={t('designer.granularity')}><Select aria-label={t('designer.granularity') + ' ' + node.ref} value={editor.granularity}
          options={['auto', '1m', '5m', '15m', '30m', '1h', '6h', '1d'].map(value => ({ value, label: value === 'auto' ? t('designer.auto') : value }))}
          onValueChange={value => patchEditor({ granularity: value as EditorDefinition['granularity'] })} /></Field>}
      </div>
      {source === 'metrics' && <MetricPicker value={editor.metricName ?? ''} range={range} index={0}
        onChange={metricName => patchEditor({ metricName })}
        onFilter={(field, value) => patchEditor({ metricFilters: [...(editor.metricFilters ?? []), { field, value }] })} />}
      {(editor.metricFilters ?? []).map((filter, index) => <div key={index} className="flex flex-wrap items-center gap-2 text-xs">
        <code className="break-all">{filter.field} = {filter.value}</code>
        <Button variant="link" size="sm" icon={<RotateCcw className="size-3.5" />} onClick={() => patchEditor({ metricFilters: editor.metricFilters?.filter((_, i) => i !== index) })}>{t('common.delete')}</Button>
      </div>)}
      <Field label={t('designer.lucene')}><WhereSuggestInput domain={source} syntax="lucene" value={editor.where}
        ariaLabel={t('designer.lucene') + ' ' + node.ref} onChange={where => patchEditor({ where })} onRun={onRun}
        testId={'designer-where-' + node.ref} from={range.from} to={range.to} denoise={false} placeholder={t('designer.luceneExample')} /></Field>
      {['series', 'category'].includes(editor.shape) && <Field label={t('designer.groupSelection')}>
        <div className="flex flex-wrap gap-x-4 gap-y-2">{DOMAIN_COLUMNS[source].filter(field => field !== 'ts' && field !== 'value' && fields.some(item => item.field === field && item.kind !== 'json')).map(field =>
          <Checkbox key={field} label={field} checked={editor.groupBy.includes(field)} onChange={event => patchEditor({ groupBy: event.target.checked ? [...editor.groupBy, field] : editor.groupBy.filter(item => item !== field) })} />)}</div>
      </Field>}
      <DesignerFieldHints range={range} source={source} />
      <details><summary className="cursor-pointer text-xs text-muted">{t('designer.generated')}</summary>
        <SqlEditor value={generated.sql} readOnly />
        <pre className="overflow-auto p-2 text-xs text-muted">{JSON.stringify(generated.parameters, null, 2)}</pre>
      </details>
    </> : node.kind === 'expression' ? <>
      {editor?.mode === 'guided' ? <div className="grid gap-3 sm:grid-cols-3">
        <Field label={t('designer.calculation')}><Select aria-label={t('designer.calculation') + ' ' + node.ref} value={editor.operation ?? 'ratio'} options={['sum', 'difference', 'ratio', 'percentage', 'reduce'].map(value => ({ value, label: t('designer.op_' + value) }))} onValueChange={value => { patchEditor({ operation: value as EditorDefinition['operation'] }); onChange({ reduce: value === 'reduce' ? node.reduce || 'avg' : '' }); }} /></Field>
        <Field label={t('designer.left')}><Select aria-label={t('designer.left') + ' ' + node.ref} value={editor.left} options={choices} onValueChange={left => patchEditor({ left })} /></Field>
        {editor.operation !== 'reduce' && <Field label={t('designer.right')}><Select aria-label={t('designer.right') + ' ' + node.ref} value={editor.right} options={choices} onValueChange={right => patchEditor({ right })} /></Field>}
      </div> : <Field label={t('designer.expression')}><Input aria-label={t('designer.expression') + ' ' + node.ref} value={node.expression} onChange={event => onChange({ expression: event.target.value })} />
        <div className="mt-2 flex flex-wrap gap-2">{preceding.map(item => <Button key={item.ref} variant="link" size="sm" icon={<Plus className="size-3.5" />} onClick={() => onChange({ expression: node.expression + item.ref })}>{item.ref}</Button>)}</div>
      </Field>}
      <Field label={t('designer.reduce')}><Select aria-label={t('designer.reduce') + ' ' + node.ref} value={node.reduce || 'none'} onValueChange={value => onChange({ reduce: value === 'none' ? '' : value as CustomPanelNode['reduce'] })} options={['none', 'sum', 'avg', 'min', 'max', 'last', 'count'].map(value => ({ value, label: value === 'none' ? t('designer.noReduce') : t('designer.agg_' + value, value) }))} /></Field>
      <code className="block text-xs text-muted">{generated.expression}</code>
    </> : <>
      <SqlEditor value={node.sql} onChange={sql => onChange({ sql })} onRun={onRun}
        schema={Object.fromEntries(Object.entries(SEARCH_FIELDS).map(([key, value]) => [key, value.map(field => field.field)]))} parameters={Object.keys(node.parameters)} />
      <Field label={t('designer.parameters')}><Textarea key={node.ref} aria-label={t('designer.parameters') + ' ' + node.ref} defaultValue={JSON.stringify(node.parameters, null, 2)} onChange={event => {
        try {
          const parameters = JSON.parse(event.target.value);
          if (!parameters || Array.isArray(parameters) || typeof parameters !== 'object' || Object.values(parameters).some(value => value !== null && !['string', 'number', 'boolean'].includes(typeof value))) throw new Error();
          onChange({ parameters }); onInvalid(false); setCompileError('');
        } catch { onInvalid(true); setCompileError('designer.invalidParameters'); }
      }} /></Field>
      <div className="grid gap-3 sm:grid-cols-4">
        <Field label={t('designer.shape')}><Select value={node.shape} options={['table', 'scalar', 'series', 'category'].map(value => ({ value, label: t('designer.' + value) }))} onValueChange={value => onChange({ shape: value as CustomPanelNode['shape'] })} /></Field>
        {node.shape !== 'table' && <Field label={t('designer.valueField')}><Input value={node.valueField} list={node.ref + '-columns'} onChange={event => onChange({ valueField: event.target.value })} /></Field>}
        {node.shape === 'series' && <Field label={t('designer.timeField')}><Input value={node.timeField} list={node.ref + '-columns'} onChange={event => onChange({ timeField: event.target.value })} /></Field>}
        {['series', 'category'].includes(node.shape) && <Field label={t('designer.groups')}><Input value={node.groupFields.join(', ')} onChange={event => onChange({ groupFields: event.target.value.split(',').map(s => s.trim()).filter(Boolean) })} /></Field>}
        <datalist id={node.ref + '-columns'}>{columns.map(column => <option key={column} value={column} />)}</datalist>
      </div>
    </>}
    <ConfirmModal open={rebuild} onOpenChange={setRebuild} title={t('designer.rebuild')} content={t('designer.rebuildHint')} onOk={() => {
      const nextEditor = { ...defaultNodeEditor(), mode: node.kind === 'sql' ? 'builder' as const : 'guided' as const, operation: 'ratio' as const, left: preceding[0]?.ref, right: preceding[1]?.ref ?? preceding[0]?.ref };
      patchEditor(nextEditor); setRebuild(false);
    }} okText={t('designer.rebuild')} cancelText={t('common.cancel')} />
  </div>;
}
