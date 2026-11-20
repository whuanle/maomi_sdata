import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import { useForm } from 'react-hook-form';
import { useNavigate } from 'react-router';
import { Download, FileUp, LayoutDashboard, Plus, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { queryDashboards, saveDashboard, deleteDashboard } from '@/api/dashboards';
import type { DashboardItem } from '@/api/types';
import { AuditCell, Button, Empty, Field, Input, Modal, Page, Popconfirm, Spin, Textarea, toast } from '@/design-system';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { emptyDashboard, normalizeDashboard } from './dashboardLayout';
import { buildDashboardExport, downloadJson, parseDashboardImport, safeFileName } from './transfer';

/** Dashboard 列表：创建后直接进入空画布，不暴露配置 JSON；导入导出为整盘 JSON 文件. */
export function DashboardsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const canWrite = useAuthStore((state) => canWriteRole(state.role));
  const [items, setItems] = useState<DashboardItem[]>([]);
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [importing, setImporting] = useState(false);
  const [importSource, setImportSource] = useState('');
  const [imported, setImported] = useState<ReturnType<typeof parseDashboardImport> | null>(null);
  const [importForm, setImportForm] = useState({ name: '', description: '' });
  const fileRef = useRef<HTMLInputElement>(null);
  const { register, handleSubmit, reset, formState: { errors } } = useForm({ defaultValues: { name: '', description: '' } });
  const load = async () => {
    setLoading(true);
    try {
      const all: DashboardItem[] = [];
      for (let page = 1; ; page++) {
        const result = await queryDashboards({ kind: 'view', page, pageSize: 100 });
        all.push(...result.data);
        if (all.length >= result.count || result.data.length === 0) break;
      }
      setItems(all);
    } catch (error) { toast.error(String(error)); }
    finally { setLoading(false); }
  };
  useEffect(() => { void load(); }, []);
  const create = handleSubmit(async (values) => {
    setSaving(true);
    try {
      const result = await saveDashboard({ ...values, kind: 'view', configJson: JSON.stringify(emptyDashboard()) });
      navigate(`/dashboards/${result.value}?edit=1`);
    } catch (error) { toast.error(String(error)); }
    finally { setSaving(false); }
  });
  /** 卡片导出：列表已带 configJson，归一化后落盘；解析失败提示且不下载. */
  const exportItem = (item: DashboardItem) => {
    try {
      const config = normalizeDashboard(item.configJson);
      downloadJson(safeFileName(item.name, 'dashboard'), buildDashboardExport(item, config));
    } catch (error) { toast.error(String(error)); }
  };
  /** 文件或粘贴内容变化即解析：成功预填名称与描述，失败留待提交时统一报错. */
  const applyImportSource = (text: string) => {
    setImportSource(text);
    setImported(null);
    if (!text.trim()) return;
    try {
      const parsed = parseDashboardImport(text);
      setImported(parsed);
      setImportForm({ name: parsed.name, description: parsed.description });
    } catch { /* 提交时提示 dashboard.importInvalid */ }
  };
  const onImportFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (file) await applyImportSource(await file.text());
    event.target.value = '';
  };
  const confirmImport = async () => {
    let parsed = imported;
    if (!parsed) {
      try { parsed = parseDashboardImport(importSource); } catch { toast.error(t('dashboard.importInvalid')); return; }
    }
    if (!importForm.name.trim()) { toast.error(t('dashboard.panelTitleRequired')); return; }
    setImporting(true);
    try {
      await saveDashboard({ name: importForm.name.trim(), description: importForm.description, kind: 'view', configJson: JSON.stringify(parsed.config) });
      setImportOpen(false);
      setImportSource(''); setImported(null); setImportForm({ name: '', description: '' });
      toast.success(t('dashboard.importSuccess'));
      await load();
    } catch (error) { toast.error(String(error)); }
    finally { setImporting(false); }
  };
  return <Page>
    <div className="flex h-full flex-col gap-5 overflow-auto">
      <div className="flex items-center gap-3">
        <Input className="max-w-lg" aria-label={t('dashboard.search')} placeholder={t('dashboard.search')} value={search} onChange={(event) => setSearch(event.target.value)} />
        {canWrite && <Button icon={<FileUp className="size-4" />} onClick={() => { setImportSource(''); setImported(null); setImportForm({ name: '', description: '' }); setImportOpen(true); }}>{t('dashboard.import')}</Button>}
        {canWrite && <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => { reset(); setOpen(true); }}>{t('dashboard.newDashboard')}</Button>}
      </div>
      {loading ? <Spin /> : <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        {items.filter((item) => item.name.toLowerCase().includes(search.toLowerCase())).map((item) => <div key={item.id} className="rounded-lg border border-border bg-surface p-4">
          <Button variant="link" icon={<LayoutDashboard className="size-4" />} onClick={() => navigate(`/dashboards/${item.id}`)}>{item.name}</Button>
          <p className="my-3 text-sm text-muted">{item.description || '—'}</p>
          <div className="flex items-end justify-between gap-2">
            <AuditCell time={item.updateTime} />
            <div className="flex items-center gap-1">
              <Button variant="link" size="sm" icon={<Download className="size-3" />} onClick={() => exportItem(item)}>{t('dashboard.export')}</Button>
              {canWrite && <Popconfirm title={t('common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={async () => { await deleteDashboard(item.id); await load(); }}>
                <Button variant="link" size="sm" danger icon={<Trash2 className="size-3" />}>{t('common.delete')}</Button>
              </Popconfirm>}
            </div>
          </div>
        </div>)}
      </div>}
      {!loading && items.length === 0 && <Empty description={t('dashboard.createFirst')} />}
    </div>
    <Modal open={open} onOpenChange={setOpen} title={t('dashboard.newDashboard')} footer={<><Button onClick={() => setOpen(false)}>{t('common.cancel')}</Button><Button loading={saving} variant="primary" onClick={() => void create()}>{t('common.create')}</Button></>}>
      <form className="flex flex-col gap-3" onSubmit={create}>
        <Field label={t('dashboard.name')} required error={errors.name?.message}><Input aria-label={t('dashboard.name')} {...register('name', { required: t('dashboard.panelTitleRequired'), maxLength: 64 })} /></Field>
        <Field label={t('dashboard.description')}><Input aria-label={t('dashboard.description')} {...register('description', { maxLength: 256 })} /></Field>
      </form>
    </Modal>
    <Modal open={importOpen} onOpenChange={setImportOpen} title={t('dashboard.importDashboard')} footer={<><Button onClick={() => setImportOpen(false)}>{t('common.cancel')}</Button><Button loading={importing} variant="primary" onClick={() => void confirmImport()}>{t('dashboard.import')}</Button></>}>
      <div className="flex flex-col gap-3">
        <Field label={t('dashboard.importSource')} error={importSource.trim() && !imported ? t('dashboard.importInvalid') : undefined}>
          <input ref={fileRef} type="file" accept=".json,application/json" className="hidden" onChange={(event) => void onImportFile(event)} />
          <div className="flex items-center gap-2">
            <Button icon={<FileUp className="size-4" />} onClick={() => fileRef.current?.click()}>{t('dashboard.importFile')}</Button>
            {imported && <span className="text-xs text-muted">{t('dashboard.panelsCount', { count: imported.config.panels.length })}</span>}
          </div>
          <Textarea aria-label={t('dashboard.importSource')} className="mt-2 min-h-32" placeholder={t('dashboard.importPlaceholder')} value={importSource} onChange={(event) => applyImportSource(event.target.value)} />
        </Field>
        <Field label={t('dashboard.name')} required><Input aria-label={t('dashboard.name')} value={importForm.name} maxLength={64} onChange={(event) => setImportForm((form) => ({ ...form, name: event.target.value }))} /></Field>
        <Field label={t('dashboard.description')}><Input aria-label={t('dashboard.description')} value={importForm.description} maxLength={256} onChange={(event) => setImportForm((form) => ({ ...form, description: event.target.value }))} /></Field>
      </div>
    </Modal>
  </Page>;
}
