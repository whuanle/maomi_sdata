import { useState } from 'react';
import { cn } from '@/utils/cn';
import { ArrowDown, ArrowUp, Copy, Download, Eye, Pencil, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { DashboardConfig, PanelConfig, PanelData } from '@/api/types';
import { Button, DashboardGrid, Popconfirm, type DashboardGridItem } from '@/design-system';
import { PanelCard } from './PanelCard';
import { GroupCard } from './GroupCard';

export function DashboardCanvas({ config, data, editable = false, loading, onLayout, onEdit, onCopy, onDelete, onMove, onGroup, onDissolve, onPreview, onExport, onGroupDrop }: {
  config: DashboardConfig;
  data: Record<string, PanelData>;
  editable?: boolean;
  loading?: boolean;
  onLayout?: (items: DashboardGridItem[]) => void;
  onEdit?: (panel: PanelConfig) => void;
  onPreview?: (panel: PanelConfig) => void;
  onExport?: (panel: PanelConfig) => void;
  onCopy?: (panel: PanelConfig) => void;
  onDelete?: (id: string) => void;
  onMove?: (panel: PanelConfig, direction: number) => void;
  onGroup?: (panel: PanelConfig) => void;
  onDissolve?: (panel: PanelConfig) => void;
  onGroupDrop?: (panelId: string, groupId: string) => void;
}) {
  const { t } = useTranslation();
  const [dropGroup, setDropGroup] = useState<string | undefined>();
  const actions = (panel: PanelConfig, member = false) => <div className="flex flex-wrap items-center gap-1">
    {onPreview && <Button variant="link" size="sm" icon={<Eye className="size-3" />} onClick={() => onPreview(panel)}>{t('dashboard.preview')}</Button>}
    {onExport && <Button variant="link" size="sm" icon={<Download className="size-3" />} onClick={() => onExport(panel)}>{t('dashboard.export')}</Button>}
    {editable && <>
    <Button variant="link" size="sm" icon={<Pencil className="size-3" />} onClick={() => panel.type === 'group' ? onGroup?.(panel) : onEdit?.(panel)}>{t('common.edit')}</Button>
    <Button variant="link" size="sm" icon={<Copy className="size-3" />} onClick={() => onCopy?.(panel)}>{t('dashboard.copy')}</Button>
    {!member && <>
      <Button variant="link" size="sm" icon={<ArrowUp className="size-3" />} onClick={() => onMove?.(panel, -1)}>{t('dashboard.moveUp')}</Button>
      <Button variant="link" size="sm" icon={<ArrowDown className="size-3" />} onClick={() => onMove?.(panel, 1)}>{t('dashboard.moveDown')}</Button>
    </>}
    {panel.type === 'group' && <Button variant="link" size="sm" icon={<Copy className="size-3" />} onClick={() => onDissolve?.(panel)}>{t('dashboard.dissolveGroup')}</Button>}
    <Popconfirm title={t(panel.type === 'group' ? 'dashboard.deleteGroupConfirm' : 'common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => onDelete?.(panel.id)}>
      <Button variant="link" size="sm" danger icon={<Trash2 className="size-3" />}>{t('common.delete')}</Button>
    </Popconfirm>
    </>}
  </div>;
  return <DashboardGrid
    editable={editable}
    items={config.panels.map((panel) => ({ i: panel.id, x: panel.layout?.x ?? 0, y: panel.layout?.y ?? 0, w: panel.layout?.w ?? 6, h: panel.layout?.h ?? 4 }))}
    onChange={onLayout}
    groupIds={config.panels.filter(panel => panel.type === 'group').map(panel => panel.id)}
    onGroupDrop={onGroupDrop}
    onGroupHover={setDropGroup}
  >
    {config.panels.map((panel) => <div key={panel.id} className={cn("min-h-0 min-w-0 rounded-lg", dropGroup === panel.id && "ring-2 ring-primary")} data-panel-id={panel.id}>
      {panel.type === 'group'
        ? <GroupCard config={panel} data={data} loading={loading} editable={editable} actions={actions(panel)} memberActions={member => actions(member, true)} />
        : <PanelCard config={panel} data={data[panel.id]} loading={loading && !data[panel.id]} draggable={editable} actions={actions(panel)} />}
    </div>)}
  </DashboardGrid>;
}
