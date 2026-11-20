import { useMemo, type MutableRefObject } from 'react';
import CodeMirror, { EditorView, keymap, type ReactCodeMirrorRef } from '@uiw/react-codemirror';
import { sql } from '@codemirror/lang-sql';
import { useTranslation } from 'react-i18next';
import { useAppStore } from '@/store/app';

interface SqlEditorProps {
  schema?: Record<string, string[]>;
  parameters?: string[];
  logicalTables?: string[];
  value: string;
  onChange?: (value: string) => void;
  /** Ctrl/Cmd+Enter 触发运行（只读展示时不传）. */
  onRun?: () => void;
  /** 编辑器视图引用（供外部在光标处插入文本）. */
  viewRef?: MutableRefObject<EditorView | null>;
  /** 只读展示（生成 SQL 查看弹层）：不可编辑、无占位提示. */
  readOnly?: boolean;
  /** 编辑器高度（默认 128px）. */
  height?: string;
}

/**
 * SQL 编辑器（CodeMirror 封装）：SQL 语法高亮、跟随应用主题明暗、Ctrl/Cmd+Enter 运行.
 */
export function SqlEditor({ value, onChange, onRun, viewRef, readOnly = false, height = '128px', schema, parameters = [], logicalTables }: SqlEditorProps) {
  const { t } = useTranslation();
  const dark = useAppStore((state) => state.themeKey === 'dark');

  const extensions = useMemo(
    () => [
      sql({ schema }),
      EditorView.contentAttributes.of({ 'aria-label': t('designer.sql') }),
      sql().language.data.of({ autocomplete: (context: { matchBefore: (pattern: RegExp) => { from: number; text: string } | null; explicit: boolean; pos: number }) => {
        const word = context.matchBefore(/[$\w{]+/);
        if (!word || (!context.explicit && !word.text)) return null;
        const names = [...new Set(['start', 'end', ...parameters])];
        return { from: word.from, options: [
          ...names.map(name => ({ label: '$' + name, type: 'variable' })),
          ...(logicalTables ?? Object.keys(schema ?? {})).map(name => ({ label: '{{' + name + '}}', type: 'type' })),
          ...['count', 'sum', 'avg', 'min', 'max', 'date_trunc', 'time_bucket', 'coalesce', 'nullif', 'json_extract_string', 'quantile_cont'].map(name => ({ label: name, type: 'function' })),
        ] };
      } }),
      EditorView.lineWrapping,
      ...(onRun
        ? [
            keymap.of([
              {
                key: 'Mod-Enter',
                preventDefault: true,
                run: () => {
                  onRun();
                  return true;
                },
              },
            ]),
          ]
        : []),
    ],
    [onRun, schema, parameters, logicalTables, t],
  );

  return (
    <CodeMirror
      value={value}
      onChange={onChange}
      editable={!readOnly}
      theme={dark ? 'dark' : 'light'}
      extensions={extensions}
      onCreateEditor={(view) => {
        if (viewRef) {
          viewRef.current = view;
        }
      }}
      height={height}
      basicSetup={{ foldGutter: false, searchKeymap: false, highlightActiveLine: false }}
      placeholder={readOnly ? undefined : t('search.sqlPlaceholder')}
      className="text-[13px]"
    />
  );
}

export type { ReactCodeMirrorRef };
