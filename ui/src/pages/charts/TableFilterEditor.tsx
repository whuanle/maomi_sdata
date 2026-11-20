import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Select } from '@/design-system';
import { searchSql } from '@/api/search';
import type { SearchSqlParameters } from '@/api/types';
import type { ChartSource } from '@/api/chartTypes';
import { buildFieldValuesSql, likePattern, useDebounced } from '@/pages/search/facets';
import { tableFields, type TableFilter } from './tableBuilder';

export function TableFilterEditor({ source, value, from, to, onChange, onRemove }: {
  source: ChartSource; value: TableFilter; from: string; to: string;
  onChange: (value: TableFilter) => void; onRemove: () => void;
}) {
  const { t } = useTranslation();
  const [choices, setChoices] = useState<string[]>([]);
  const [focused, setFocused] = useState(false);
  const [active, setActive] = useState(-1);
  const [failed, setFailed] = useState(false);
  const typed = useDebounced(value.value, 250);
  const field = tableFields(source).find((item) => item.field === value.field);
  useEffect(() => {
    let cancelled = false;
    setChoices([]); setFailed(false); setActive(-1);
    if (!focused || !field || ['exists', 'missing'].includes(value.operator)) return;
    const parameters: SearchSqlParameters = likePattern(typed) ? { key: likePattern(typed)! } : {};
    void searchSql({ sql: buildFieldValuesSql(source, false, field.field, 10, typed, parameters), from, to, parameters, rowLimit: 10 })
      .then((result) => { if (!cancelled) setChoices(result.rows.map((row) => String(row[0]))); })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, [source, value.field, value.operator, typed, from, to, focused]);
  const operators = field?.kind === 'number' ? ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'exists', 'missing'] : ['eq', 'neq', 'contains', 'exists', 'missing'];
  return <div className="space-y-1">
    <div className="flex flex-wrap items-center gap-2">
      <Select className="w-40" aria-label={t('charts.tableFilterField')} value={value.field} options={tableFields(source).map((item) => ({ value: item.field, label: item.field }))} onValueChange={(field) => onChange({ ...value, field, operator: 'eq', value: '' })} />
      <Select className="w-32" aria-label={t('charts.tableFilterOperator')} value={value.operator} options={operators.map((operator) => ({ value: operator, label: t('charts.tableOp_' + operator) }))} onValueChange={(operator) => onChange({ ...value, operator: operator as TableFilter['operator'] })} />
      {!['exists', 'missing'].includes(value.operator) && <Input className="min-w-40 flex-1" aria-label={t('charts.tableFilterValue')} placeholder={t('charts.tableValueHint')} value={value.value} role="combobox" aria-autocomplete="list" aria-expanded={focused && choices.length > 0} aria-controls={'table-values-' + value.id} aria-activedescendant={active >= 0 ? 'table-value-' + value.id + '-' + active : undefined} onKeyDown={(event) => {
        if (event.nativeEvent.isComposing) return;
        if (event.key === 'Escape') { setFocused(false); setActive(-1); }
        if (choices.length && ['ArrowDown', 'ArrowUp'].includes(event.key)) { event.preventDefault(); setFocused(true); setActive((index) => (index + (event.key === 'ArrowDown' ? 1 : choices.length - 1) + choices.length) % choices.length); }
        if (focused && active >= 0 && ['Enter', 'Tab'].includes(event.key)) { event.preventDefault(); onChange({ ...value, value: choices[active] }); setFocused(false); setActive(-1); }
      }} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)} onChange={(event) => onChange({ ...value, value: event.target.value })} />}
      <Button variant="link" size="sm" onClick={onRemove}>{t('common.delete')}</Button>
    </div>
    {focused && <div className="flex flex-wrap gap-1" role="listbox" id={'table-values-' + value.id} aria-label={t('charts.tableValueSuggestions')}>
      {choices.map((choice, index) => <Button role="option" aria-selected={active === index} id={'table-value-' + value.id + '-' + index} key={choice} size="sm" variant="secondary" onMouseDown={(event) => event.preventDefault()} onClick={() => onChange({ ...value, value: choice })}>{choice}</Button>)}
      <span className="text-xs text-muted">{t(failed ? 'charts.tableSuggestionsFailed' : 'charts.tableValueHint')}</span>
    </div>}
  </div>;
}
