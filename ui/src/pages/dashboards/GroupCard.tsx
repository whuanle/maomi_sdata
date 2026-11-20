import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import type { PanelConfig, PanelData } from '@/api/types';
import { Tabs, TabsList, TabsTrigger } from '@/design-system';
import { PanelCardBody } from './PanelCard';

export function GroupCard({ config, data, loading, editable, actions, memberActions }: {
  config: PanelConfig; data: Record<string, PanelData>; loading?: boolean; editable?: boolean;
  actions?: ReactNode; memberActions?: (panel: PanelConfig) => ReactNode;
}) {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const selected = params.get('tab.' + config.id) ?? config.defaultPanelId;
  const setSelected = (value: string) => setParams(previous => {
    const next = new URLSearchParams(previous); next.set('tab.' + config.id, value); return next;
  }, { replace: true });
  const members = config.panels ?? [];
  const active = members.find(panel => panel.id === selected) ?? members.find(panel => panel.id === config.defaultPanelId) ?? members[0];
  return <div className="flex h-full min-w-0 flex-col rounded-lg border border-border bg-surface">
    <div className={`flex shrink-0 flex-wrap items-center justify-between gap-2 border-b border-border px-3 py-2 ${editable ? 'dashboard-drag-handle cursor-move' : ''}`}>
      <span className="truncate text-sm font-medium text-fg">{config.title}</span>{actions}
    </div>
    {active && <>
      <Tabs value={active.id} onValueChange={setSelected} className="min-w-0 shrink-0 overflow-x-auto">
        <TabsList>{members.map(panel => <TabsTrigger key={panel.id} value={panel.id}>{panel.title}</TabsTrigger>)}</TabsList>
      </Tabs>
      {editable && <div className="flex shrink-0 flex-wrap items-center gap-1 px-3">{memberActions?.(active)}</div>}
      <div className="min-h-0 flex-1 px-3 py-2" role="tabpanel" aria-label={active.title}>
        <PanelCardBody key={active.id} config={active} data={data[active.id]} loading={loading && !data[active.id]} />
      </div>
    </>}
    {!active && <p className="p-3 text-muted">{t('dashboard.emptyGroupHint')}</p>}
  </div>;
}
