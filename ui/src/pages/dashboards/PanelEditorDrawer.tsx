import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Eye, Plus, Trash2 } from 'lucide-react';
import { Button, Drawer, Field, Input, Select, Switch, Textarea, MARKDOWN_MAX_LENGTH, toast } from '@/design-system';
import { previewPanel } from '@/api/dashboards';
import { listMetricNames } from '@/api/metrics';
import type { PanelConfig, PanelData, PanelDsl, PanelQueryKind, PanelType } from '@/api/types';
import { PanelCardBody } from './PanelCard';
import { resolvePanelVisualization } from '@/pages/charts/panelDefinition';

const TYPE_OPTIONS: PanelType[] = ['line', 'area', 'bar', 'stat', 'table', 'markdown'];
const UNIT_OPTIONS = ['short', 'ms', 'bytes', 'percent', 'ops'];
const AGG_OPTIONS = ['avg', 'sum', 'max', 'min', 'count', 'last', 'p95'];
const STEP_OPTIONS = ['10', '30', '60', '300', '900', '3600'];
const DSL_STEP_OPTIONS = ['30s', '1m', '5m', '15m', '1h'];
const DSL_FUNCS = ['count', 'avg', 'sum', 'min', 'max', 'quantile'];
const DOMAIN_OPTIONS = ['logs', 'traces', 'metrics', 'iot'];
const FILTER_OPS = ['eq', 'neq', 'prefix', 'gt', 'gte', 'lt', 'lte'];
const DSL_FIELD_HINTS: Record<string, string> = {
  logs: 'service / severity / attributes.xxx',
  traces: 'service / name / status / attributes.xxx',
  metrics: 'name / kind / labels.xxx',
  iot: 'device_key / signal',
};

/** dsl 编辑形态：时间序列（bucket）或聚合表格. */
type DslMode = 'series' | 'table';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** 编辑的面板（null = 新建）. */
  initial: PanelConfig | null;
  from?: string;
  to?: string;
  onSaved: (panel: PanelConfig) => void;
  onPreview?: () => void;
}

let panelSeq = 0;

/** 新面板默认配置. */
function newPanel(): PanelConfig {
  panelSeq += 1;
  return {
    id: `p${Date.now().toString(36)}${panelSeq}`,
    title: '',
    type: 'line',
    layout: { w: 6, h: 4 },
    unit: 'short',
    showLegend: true,
    query: { kind: 'metric', name: '', labels: {}, aggregation: 'avg', stepSeconds: 60, maxSeries: 20 },
  };
}

/** 归一化编辑面板（旧协议面板带出兼容字段）. */
function normalize(initial: PanelConfig | null): PanelConfig {
  const base = initial ? JSON.parse(JSON.stringify(initial)) as PanelConfig : newPanel();
  if (!base.layout) {
    base.layout = { w: 6, h: 4 };
  }

  if (base.type === 'markdown') return { ...base, query: undefined, sql: undefined, queryId: undefined, explorer: undefined, editor: undefined, visualization: undefined };

  if (!base.query && (base.sql || base.queryId)) {
    base.query = { kind: 'sql', sql: base.sql ?? '', queryId: base.queryId ?? 0 };
  }

  if (!base.query) {
    base.query = { kind: 'metric', name: '', labels: {}, aggregation: 'avg', stepSeconds: 60, maxSeries: 20 };
  }

  return base;
}

/** 校验面板配置；返回错误消息或 null. */
function validatePanel(config: PanelConfig): string | null {
  const query = config.query;
  if (!config.title.trim()) {
    return 'dashboard.panelTitleRequired';
  }

  if (config.type === 'markdown') return (config.markdown?.length ?? 0) > MARKDOWN_MAX_LENGTH ? 'charts.markdownTooLong' : null;

  if (!query) {
    return 'dashboard.queryRequired';
  }

  if (query.kind === 'metric' && !query.name) {
    return 'dashboard.metricNameRequired';
  }

  if (query.kind === 'promql' && !query.expr?.trim()) {
    return 'dashboard.exprRequired';
  }

  if (query.kind === 'dsl') {
    const dsl = query.dsl;
    if (!dsl || (!dsl.bucket && (!dsl.aggregations || dsl.aggregations.length === 0))) {
      return 'dashboard.dslRequired';
    }

    for (const agg of dsl.aggregations ?? []) {
      if (agg.func !== 'count' && !agg.field) {
        return 'dashboard.aggFieldRequired';
      }
    }

    if (dsl.bucket && dsl.bucket.func !== 'count' && !dsl.bucket.field) {
      return 'dashboard.aggFieldRequired';
    }
  }

  if (query.kind === 'sql' && !query.sql?.trim() && !query.queryId) {
    return 'dashboard.queryRequired';
  }

  return null;
}

/**
 * 面板编辑器抽屉：基本信息 + 查询构建器 + 实时预览（防抖调 preview-panel）.
 */
export function PanelEditorDrawer({ open, onOpenChange, initial, onSaved, onPreview, from, to }: Props) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState<PanelConfig>(() => normalize(initial));
  const [metricNames, setMetricNames] = useState<string[]>([]);
  const [preview, setPreview] = useState<PanelData | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const timerRef = useRef<number | null>(null);
  const previewGeneration = useRef(0);
  const query = draft.query ?? { kind: 'metric' as PanelQueryKind };
  const dslMode: DslMode = query.dsl?.bucket ? 'series' : 'table';

  useEffect(() => {
    if (open) {
      setDraft(normalize(initial));
      setPreview(null);
      if (initial?.type !== 'markdown') void listMetricNames().then(setMetricNames).catch(() => undefined);
    }
  }, [open, initial]);

  const runPreview = useCallback(async (config: PanelConfig) => {
    const generation = ++previewGeneration.current;
    setPreviewing(true);
    try {
      const data = await previewPanel(config, from, to);
      if (generation === previewGeneration.current) setPreview(data);
    } catch (err) {
      if (generation === previewGeneration.current) setPreview({ id: config.id, series: [], columns: [], rows: [], elapsedMs: 0, error: err instanceof Error ? err.message : String(err) });
    } finally {
      if (generation === previewGeneration.current) setPreviewing(false);
    }
  }, [from, to]);

  // 查询配置变化 → 防抖预览（抽屉关闭时不触发）
  useEffect(() => {
    if (!open || draft.type === 'markdown') {
      setPreviewing(false);
      return undefined;
    }

    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
    }

    timerRef.current = window.setTimeout(() => void runPreview(draft), 600);
    return () => {
      previewGeneration.current++;
      if (timerRef.current !== null) {
        window.clearTimeout(timerRef.current);
        timerRef.current = null;
      }
    };
  }, [open, draft, runPreview]);

  const patchQuery = (patch: Partial<NonNullable<PanelConfig['query']>>) => {
    setDraft((prev) => {
      const changed = patch.sql !== undefined || (patch.kind !== undefined && patch.kind !== prev.query?.kind);
      const changedKind = patch.kind !== undefined && patch.kind !== prev.query?.kind;
      return { ...prev, editor: changed ? undefined : prev.editor,
        explorer: changed ? undefined : prev.explorer,
        visualization: changedKind ? undefined : changed ? resolvePanelVisualization(prev) : prev.visualization,
        query: { ...(prev.query ?? { kind: 'metric' }), ...patch, ...(changed ? { numberTrend: undefined } : {}) } };
    });
  };

  const patchDsl = (patch: Partial<NonNullable<NonNullable<PanelConfig['query']>['dsl']>>) => {
    setDraft((prev) => ({
      ...prev,
      query: { ...(prev.query ?? { kind: 'metric' }), dsl: { ...(prev.query?.dsl ?? {}), ...patch } },
    }));
  };

  /** 切换 dsl 形态：时间序列（bucket）↔ 聚合表格. */
  const switchDslMode = (mode: DslMode) => {
    const dsl = query.dsl ?? {};
    if (mode === 'series') {
      patchDsl({ bucket: dsl.bucket ?? { step: '1m', func: 'count' }, aggregations: undefined });
    } else {
      patchDsl({ aggregations: dsl.aggregations?.length ? dsl.aggregations : [{ func: 'count', alias: 'total' }], bucket: null });
    }
  };

  const save = () => {
    const error = validatePanel(draft);
    if (error) {
      toast.error(t(error));
      return;
    }

    onSaved(draft.type === 'markdown' ? { ...draft, query: undefined, sql: undefined, queryId: undefined, explorer: undefined, editor: undefined, visualization: undefined } : draft);
    onOpenChange(false);
  };

  const labelRows = Object.entries(query.labels ?? {});
  const setLabelRow = (key: string, nextKey: string, nextValue: string) => {
    const labels: Record<string, string> = {};
    for (const [k, v] of Object.entries(query.labels ?? {})) {
      labels[k === key ? nextKey : k] = k === key ? nextValue : v;
    }

    patchQuery({ labels });
  };

  return (
    <Drawer
      open={open}
      onOpenChange={onOpenChange}
      title={initial ? t('dashboard.editPanel') : t('dashboard.addPanel')}
      width={720}
      footer={(
        <div className="flex justify-end gap-2">
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          {onPreview && <Button icon={<Eye className="size-3.5" />} onClick={onPreview}>{t('dashboard.preview')}</Button>}
          <Button variant="primary" onClick={save}>{t('dashboard.savePanel')}</Button>
        </div>
      )}
    >
      <div className="flex flex-col gap-4">
        {/* 基本信息 */}
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('dashboard.panelTitle')} required>
            <Input value={draft.title} onChange={(event) => setDraft((prev) => ({ ...prev, title: event.target.value }))} placeholder={t('dashboard.panelTitle')} />
          </Field>
          <Field label={t('dashboard.type')}>
            <div className="flex gap-1">
              {TYPE_OPTIONS.map((type) => (
                <button
                  key={type}
                  type="button"
                  className={`h-8 flex-1 rounded-md border text-xs transition-colors ${draft.type === type ? 'border-primary bg-primary-soft text-primary' : 'border-border text-muted hover:border-border-strong hover:text-fg'}`}
                  onClick={() => setDraft((prev) => ({ ...prev, type, explorer: type === prev.type ? prev.explorer : undefined, editor: type === prev.type ? prev.editor : undefined, visualization: type === prev.type ? prev.visualization : undefined }))}
                >
                  {t(`dashboard.type_${type}`)}
                </button>
              ))}
            </div>
          </Field>
          <Field label={t('dashboard.width')}>
            <Select
              value={String(draft.layout?.w ?? 6)}
              onValueChange={(value) => setDraft((prev) => ({ ...prev, layout: { ...(prev.layout ?? { h: 4 }), w: Number(value), x: Math.min(prev.layout?.x ?? 0, 12 - Number(value)) } }))}
              options={[3, 4, 6, 8, 12].map((w) => ({ value: String(w), label: `${w}/12` }))}
            />
          </Field>
          <Field label={t('dashboard.height')}>
            <Select
              value={String(draft.layout?.h ?? 4)}
              onValueChange={(value) => setDraft((prev) => ({ ...prev, layout: { ...(prev.layout ?? { w: 6 }), h: Number(value) } }))}
              options={[2, 3, 4, 5, 6, 7, 8].map((h) => ({ value: String(h), label: `${h}` }))}
            />
          </Field>
          {draft.type !== 'markdown' && <><Field label={t('dashboard.unit')}>
            <Select
              value={draft.unit ?? 'short'}
              onValueChange={(value) => setDraft((prev) => ({ ...prev, unit: value }))}
              options={UNIT_OPTIONS.map((unit) => ({ value: unit, label: t(`dashboard.unit_${unit}`) }))}
            />
          </Field>
          <Field label={t('dashboard.legend')}>
            <div className="flex h-8 items-center">
              <Switch checked={draft.showLegend !== false} onChange={(checked) => setDraft((prev) => ({ ...prev, showLegend: checked }))} />
            </div>
          </Field></>}
        </div>

        {draft.type === 'markdown' ? <Field label={t('charts.markdownContent')}>
          <Textarea aria-label={t('charts.markdownContent')} value={draft.markdown ?? ''} onChange={(event) => setDraft((prev) => ({ ...prev, markdown: event.target.value }))} maxLength={MARKDOWN_MAX_LENGTH} placeholder="Markdown" className="h-[200px] font-mono text-[13px]" />
        </Field> : <>
        {/* 数据来源 */}
        <Field label={t('dashboard.dataSource')}>
          <Select
            value={query.kind}
            onValueChange={(value) => {
              const kind = value as PanelQueryKind;
              const patch: Partial<NonNullable<PanelConfig['query']>> = { kind };
              // 切换形态时初始化默认查询体，保证预览即刻可用
              if (kind === 'dsl' && !query.dsl) {
                patch.dsl = { bucket: { step: '1m', func: 'count' }, groupBy: [], filters: [] };
              }

              if (kind === 'metric' && !query.name) {
                patch.name = '';
              }

              patchQuery(patch);
            }}
            options={[
              { value: 'metric', label: t('dashboard.kindMetric') },
              { value: 'dsl', label: t('dashboard.kindDsl') },
              { value: 'promql', label: t('dashboard.kindPromql') },
              { value: 'sql', label: t('dashboard.kindSql') },
            ]}
          />
        </Field>

        {/* metric 构建器 */}
        {query.kind === 'metric' && (
          <div className="flex flex-col gap-3 rounded-lg border border-border p-3">
            <div className="grid grid-cols-2 gap-3">
              <Field label={t('metrics.name')} required>
                <Select
                  value={query.name ?? ''}
                  onValueChange={(value) => patchQuery({ name: value })}
                  placeholder="http_requests_total"
                  options={metricNames.map((name) => ({ value: name, label: name }))}
                />
              </Field>
              <Field label={t('metrics.aggregation')}>
                <Select
                  value={query.aggregation ?? 'avg'}
                  onValueChange={(value) => patchQuery({ aggregation: value })}
                  options={AGG_OPTIONS.map((agg) => ({ value: agg, label: agg }))}
                />
              </Field>
              <Field label={t('metrics.step')}>
                <Select
                  value={String(query.stepSeconds ?? 60)}
                  onValueChange={(value) => patchQuery({ stepSeconds: Number(value) })}
                  options={STEP_OPTIONS.map((step) => ({ value: step, label: `${step}s` }))}
                />
              </Field>
              <Field label={t('metrics.series')}>
                <Input
                  type="number"
                  value={String(query.maxSeries ?? 20)}
                  onChange={(event) => patchQuery({ maxSeries: Number(event.target.value) || 20 })}
                />
              </Field>
            </div>
            <div>
              <div className="mb-1.5 text-sm text-muted">{t('metrics.labels')}</div>
              <div className="flex flex-col gap-1.5">
                {labelRows.map(([key, value]) => (
                  <div key={key} className="flex items-center gap-1.5">
                    <Input className="w-40" value={key} onChange={(event) => setLabelRow(key, event.target.value, value)} placeholder="service" />
                    <Input className="flex-1" value={value} onChange={(event) => setLabelRow(key, key, event.target.value)} placeholder="api-gateway" />
                    <Button
                      variant="link"
                      size="sm"
                      danger
                      icon={<Trash2 className="size-3.5" aria-hidden />}
                      onClick={() => {
                        const labels = { ...(query.labels ?? {}) };
                        delete labels[key];
                        patchQuery({ labels });
                      }}
                    >
                      {t('common.delete')}
                    </Button>
                  </div>
                ))}
                <Button
                  variant="link"
                  size="sm"
                  icon={<Plus className="size-3.5" aria-hidden />}
                  onClick={() => patchQuery({ labels: { ...(query.labels ?? {}), '': '' } })}
                >
                  {t('dashboard.addLabel')}
                </Button>
              </div>
            </div>
          </div>
        )}

        {/* dsl 构建器 */}
        {query.kind === 'dsl' && (
          <div className="flex flex-col gap-3 rounded-lg border border-border p-3">
            <div className="flex items-center gap-2">
              <Select
                className="w-36"
                value={query.domain ?? 'logs'}
                onValueChange={(value) => patchQuery({ domain: value })}
                options={DOMAIN_OPTIONS.map((domain) => ({ value: domain, label: domain }))}
              />
              <div className="flex gap-1">
                {(['series', 'table'] as DslMode[]).map((mode) => (
                  <button
                    key={mode}
                    type="button"
                    className={`h-8 rounded-md border px-3 text-xs transition-colors ${dslMode === mode ? 'border-primary bg-primary-soft text-primary' : 'border-border text-muted hover:border-border-strong hover:text-fg'}`}
                    onClick={() => switchDslMode(mode)}
                  >
                    {t(mode === 'series' ? 'dashboard.dslSeries' : 'dashboard.dslTable')}
                  </button>
                ))}
              </div>
            </div>

            {dslMode === 'series' && (
              <div className="grid grid-cols-2 gap-3">
                <Field label={t('dashboard.bucketStep')}>
                  <Select
                    value={query.dsl?.bucket?.step ?? '1m'}
                    onValueChange={(value) => patchDsl({ bucket: { ...(query.dsl?.bucket ?? { func: 'count' }), step: value } })}
                    options={DSL_STEP_OPTIONS.map((step) => ({ value: step, label: step }))}
                  />
                </Field>
                <Field label={t('dashboard.bucketFunc')}>
                  <div className="flex gap-1.5">
                    <Select
                      className="w-28"
                      value={query.dsl?.bucket?.func ?? 'count'}
                      onValueChange={(value) => patchDsl({ bucket: { ...(query.dsl?.bucket ?? { step: '1m', func: 'count' }), func: value } })}
                      options={DSL_FUNCS.map((func) => ({ value: func, label: func }))}
                    />
                    {(query.dsl?.bucket?.func ?? 'count') !== 'count' && (
                      <Input
                        className="flex-1"
                        value={query.dsl?.bucket?.field ?? ''}
                        onChange={(event) => patchDsl({ bucket: { ...(query.dsl?.bucket ?? { step: '1m', func: 'count' }), field: event.target.value } })}
                        placeholder={t('dashboard.aggField')}
                      />
                    )}
                  </div>
                </Field>
              </div>
            )}

            {dslMode === 'table' && (
              <div>
                <div className="mb-1.5 text-sm text-muted">{t('dashboard.aggregations')}</div>
                <div className="flex flex-col gap-1.5">
                  {(query.dsl?.aggregations ?? []).map((agg, index) => (
                    <div key={index} className="flex items-center gap-1.5">
                      <Select
                        className="w-28"
                        value={agg.func}
                        onValueChange={(value) => {
                          const aggregations = [...(query.dsl?.aggregations ?? [])];
                          aggregations[index] = { ...agg, func: value };
                          patchDsl({ aggregations });
                        }}
                        options={DSL_FUNCS.map((func) => ({ value: func, label: func }))}
                      />
                      <Input
                        className="flex-1"
                        value={agg.field ?? ''}
                        disabled={agg.func === 'count'}
                        onChange={(event) => {
                          const aggregations = [...(query.dsl?.aggregations ?? [])];
                          aggregations[index] = { ...agg, field: event.target.value };
                          patchDsl({ aggregations });
                        }}
                        placeholder={t('dashboard.aggField')}
                      />
                      <Input
                        className="w-28"
                        value={agg.alias ?? ''}
                        onChange={(event) => {
                          const aggregations = [...(query.dsl?.aggregations ?? [])];
                          aggregations[index] = { ...agg, alias: event.target.value };
                          patchDsl({ aggregations });
                        }}
                        placeholder={t('dashboard.aggAlias')}
                      />
                      <Button
                        variant="link"
                        size="sm"
                        danger
                        icon={<Trash2 className="size-3.5" aria-hidden />}
                        onClick={() => patchDsl({ aggregations: (query.dsl?.aggregations ?? []).filter((_agg, i) => i !== index) })}
                      >
                        {t('common.delete')}
                      </Button>
                    </div>
                  ))}
                  <Button
                    variant="link"
                    size="sm"
                    icon={<Plus className="size-3.5" aria-hidden />}
                    onClick={() => patchDsl({ aggregations: [...(query.dsl?.aggregations ?? []), { func: 'count', alias: `agg${(query.dsl?.aggregations?.length ?? 0) + 1}` }] })}
                  >
                    {t('dashboard.addAggregation')}
                  </Button>
                </div>
              </div>
            )}

            <Field label={t('dashboard.groupBy')} help={t('dashboard.groupByHint')}>
              <Input
                value={(query.dsl?.groupBy ?? []).join(', ')}
                onChange={(event) => patchDsl({ groupBy: event.target.value.split(',').map((item) => item.trim()).filter(Boolean) })}
                placeholder="service, severity"
              />
            </Field>

            <div>
              <div className="mb-1.5 text-sm text-muted">{t('dashboard.filters')}</div>
              <DslFilterRows dsl={query.dsl} patchDsl={patchDsl} help={DSL_FIELD_HINTS[query.domain ?? 'logs'] ?? ''} />
            </div>
          </div>
        )}

        {/* promql 构建器 */}
        {query.kind === 'promql' && (
          <div className="flex flex-col gap-3 rounded-lg border border-border p-3">
            <Field label={t('dashboard.expr')} required help={t('dashboard.exprHint')}>
              <Textarea
                rows={3}
                className="font-mono text-[13px]"
                value={query.expr ?? ''}
                onChange={(event) => patchQuery({ expr: event.target.value })}
                placeholder='sum by (service) (rate(http_requests_total[5m]))'
              />
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label={t('metrics.step')}>
                <Select
                  value={String(query.stepSeconds ?? 60)}
                  onValueChange={(value) => patchQuery({ stepSeconds: Number(value) })}
                  options={STEP_OPTIONS.map((step) => ({ value: step, label: `${step}s` }))}
                />
              </Field>
              <Field label={t('metrics.series')}>
                <Input
                  type="number"
                  value={String(query.maxSeries ?? 20)}
                  onChange={(event) => patchQuery({ maxSeries: Number(event.target.value) || 20 })}
                />
              </Field>
            </div>
          </div>
        )}

        {/* sql 构建器 */}
        {query.kind === 'sql' && (
          <div className="rounded-lg border border-border p-3">
            <Field label="SQL" required help={t('dashboard.sqlHint')}>
              <Textarea
                rows={4}
                className="font-mono text-[13px]"
                value={query.sql ?? ''}
                onChange={(event) => patchQuery({ sql: event.target.value })}
                placeholder="SELECT count(*) AS total FROM {{logs}}"
              />
            </Field>
          </div>
        )}

        </>}
        {/* 实时预览 */}
        <div>
          <div className="mb-1.5 flex items-center gap-2 text-sm text-muted">
            {t(draft.type === 'markdown' ? 'charts.markdownPreview' : 'dashboard.preview')}
            {draft.type !== 'markdown' && preview && !preview.error && <span className="text-[11px] text-faint">{preview.elapsedMs}ms</span>}
            {previewing && <span className="text-[11px] text-faint">…</span>}
          </div>
          <div className="h-[240px] rounded-lg border border-border p-2">
            <PanelCardBody config={draft} data={preview} loading={previewing} />
          </div>
        </div>
      </div>
    </Drawer>
  );
}

/** DSL 过滤条件行（动态增删）. */
function DslFilterRows({ dsl, patchDsl, help }: {
  dsl?: PanelDsl | null;
  patchDsl: (patch: { filters?: { field: string; op: string; value: unknown }[] }) => void;
  help: string;
}) {
  const { t } = useTranslation();
  const filters = dsl?.filters ?? [];
  return (
    <div className="flex flex-col gap-1.5">
      {filters.map((filter, index) => (
        <div key={index} className="flex items-center gap-1.5">
          <Input
            className="w-44"
            value={filter.field}
            onChange={(event) => {
              const next = [...filters];
              next[index] = { ...filter, field: event.target.value };
              patchDsl({ filters: next });
            }}
            placeholder={help}
          />
          <Select
            className="w-24"
            value={filter.op}
            onValueChange={(value) => {
              const next = [...filters];
              next[index] = { ...filter, op: value };
              patchDsl({ filters: next });
            }}
            options={FILTER_OPS.map((op) => ({ value: op, label: op }))}
          />
          <Input
            className="flex-1"
            value={String(filter.value ?? '')}
            onChange={(event) => {
              const next = [...filters];
              next[index] = { ...filter, value: event.target.value };
              patchDsl({ filters: next });
            }}
          />
          <Button
            variant="link"
            size="sm"
            danger
            icon={<Trash2 className="size-3.5" aria-hidden />}
            onClick={() => patchDsl({ filters: filters.filter((_filter, i) => i !== index) })}
          >
            {t('common.delete')}
          </Button>
        </div>
      ))}
      <Button
        variant="link"
        size="sm"
        icon={<Plus className="size-3.5" aria-hidden />}
        onClick={() => patchDsl({ filters: [...filters, { field: '', op: 'eq', value: '' }] })}
      >
        {t('dashboard.addFilter')}
      </Button>
    </div>
  );
}
