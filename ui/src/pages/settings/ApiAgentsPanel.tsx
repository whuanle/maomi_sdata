import { useCallback, useEffect, useRef, useState } from 'react';
import { Copy, Eye, Plus, Power, RefreshCw, RotateCw, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useForm } from 'react-hook-form';
import { fetchApiKeys, manageApiKey } from '@/api/apiKeys';
import type { ApiKeyItem, ManageApiKey } from '@/api/types';
import { AuditCell, Button, Field, Input, Modal, Popconfirm, Select, toast } from '@/design-system';

/** Revealed secrets are temporary and cleared when the dialog closes. */
export function ApiAgentsPanel() {
  const { t } = useTranslation();
  const [items, setItems] = useState<ApiKeyItem[]>([]);
  const [failed, setFailed] = useState(false);
  const [pending, setPending] = useState(false);
  const [kind, setKind] = useState<ApiKeyItem['kind'] | null>(null);
  const [secret, setSecret] = useState('');
  const [secretOpen, setSecretOpen] = useState(false);
  const secretRequest = useRef(0);
  useEffect(() => () => { secretRequest.current++; }, []);
  const closeSecret = () => { secretRequest.current++; setSecret(''); setSecretOpen(false); };
  const [client, setClient] = useState('generic');
  const { register, reset, handleSubmit, formState: { errors } } = useForm<{ name: string }>();
  const load = useCallback(async () => {
    try { setItems(await fetchApiKeys()); setFailed(false); }
    catch { setFailed(true); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  const run = async (command: ManageApiKey) => {
    const request = ++secretRequest.current;
    const showsSecret = ['create', 'rotate', 'reveal'].includes(command.action);
    setSecret('');
    if (showsSecret) setKind(null);
    setSecretOpen(showsSecret);
    setPending(true);
    try {
      const result = await manageApiKey(command);
      if (request === secretRequest.current) {
        setKind(null);
        if (result.secret) setSecret(result.secret);
      }
      if (command.action !== 'reveal') await load();
    } catch (error) {
      if (request === secretRequest.current) { setSecret(''); setSecretOpen(false); }
      toast.error(error instanceof Error ? error.message : t('apiAgents.failed'));
    }
    finally { setPending(false); }
  };
  const copy = async (value: string) => {
    try { await navigator.clipboard.writeText(value); toast.success(t('apiAgents.copied')); }
    catch { toast.error(t('apiAgents.copyFailed')); }
  };
  const endpoint = window.location.origin + '/api/mcp';
  const configuration = client === 'claude'
    ? `claude mcp add sdata --transport http "${endpoint}" --header "Authorization: Bearer <AGENT_KEY>"`
    : client === 'codex'
      ? `[mcp_servers.sdata]\nurl = "${endpoint}"\nbearer_token_env_var = "SDATA_AGENT_KEY"`
      : JSON.stringify({ [client === 'vscode' ? 'servers' : 'mcpServers']: { sdata: { ...(client === 'vscode' ? { type: 'http' } : {}), url: endpoint, headers: { Authorization: 'Bearer <AGENT_KEY>' } } } }, null, 2);
  return <div className="space-y-5">
    <p className="text-sm text-muted">{t('apiAgents.description')}</p>
    {failed && <div role="alert" className="flex items-center gap-2 text-error">{t('apiAgents.failed')}<Button variant="link" size="sm" icon={<RefreshCw className="size-4" />} onClick={() => void load()}>{t('settings.retry')}</Button></div>}
    {(['ingestion', 'agent'] as const).map(type => <section key={type} className="space-y-3 rounded-lg border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center justify-between gap-2"><h2 className="text-sm font-semibold">{t('apiAgents.' + type)}</h2>
        <Button icon={<Plus className="size-4" />} disabled={pending} onClick={() => { reset({ name: '' }); setKind(type); }}>{t('apiAgents.create')}</Button>
      </div>
      <p className="text-sm text-muted">{t('apiAgents.' + type + 'Hint')}</p>
      <div className="overflow-x-auto"><table className="w-full text-left text-sm">
        <thead><tr className="border-b border-border text-muted"><th className="p-2">{t('apiAgents.name')}</th><th className="p-2">{t('apiAgents.prefix')}</th><th className="p-2">{t('apiAgents.status')}</th><th className="p-2">{t('common.createTime')}</th><th className="p-2">{t('common.updateTime')}</th><th className="p-2">{t('apiAgents.actions')}</th></tr></thead>
        <tbody>{items.filter(item => item.kind === type).map(item => <tr key={item.id} className="border-b border-border">
          <td className="p-2">{item.name}</td><td className="p-2 font-mono">{item.prefix || '••••'}…</td><td className="p-2">{t(item.enabled ? 'apiAgents.enabled' : 'apiAgents.disabled')}</td>
          <td className="p-2"><AuditCell time={item.createTime} user={item.createUserId} /></td><td className="p-2"><AuditCell time={item.updateTime ?? undefined} user={item.updateUserId} /></td>
          <td className="p-2"><div className="flex flex-wrap gap-1">
            <Button variant="link" size="sm" disabled={pending || !item.canReveal} title={item.canReveal ? undefined : t('apiAgents.legacyHint')} icon={<Eye className="size-3.5" />} onClick={() => void run({ kind: type, id: item.id, action: 'reveal' })}>{t('apiAgents.view')}</Button>
            {!item.canReveal && <span className="self-center text-xs text-muted">{t('apiAgents.legacyHint')}</span>}
            <Popconfirm title={t('apiAgents.changeConfirm')} okText={t('common.save')} cancelText={t('common.cancel')} onConfirm={() => run({ kind: type, id: item.id, action: 'enable', enabled: !item.enabled })}>
              <Button variant="link" size="sm" disabled={pending} icon={<Power className="size-3.5" />}>{t(item.enabled ? 'apiAgents.disable' : 'apiAgents.enable')}</Button>
            </Popconfirm>
            <Popconfirm title={t('apiAgents.rotateConfirm')} okText={t('apiAgents.rotate')} cancelText={t('common.cancel')} onConfirm={() => run({ kind: type, id: item.id, action: 'rotate' })}>
              <Button variant="link" size="sm" danger disabled={pending} icon={<RotateCw className="size-3.5" />}>{t('apiAgents.rotate')}</Button>
            </Popconfirm>
            <Popconfirm title={t('common.confirmDelete')} okText={t('common.delete')} cancelText={t('common.cancel')} onConfirm={() => run({ kind: type, id: item.id, action: 'delete' })}>
              <Button variant="link" size="sm" danger disabled={pending} icon={<Trash2 className="size-3.5" />}>{t('common.delete')}</Button>
            </Popconfirm>
          </div></td>
        </tr>)}</tbody>
      </table></div>
    </section>)}
    <section className="space-y-3 rounded-lg border border-border bg-surface p-4">
      <h2 className="text-sm font-semibold">{t('apiAgents.connect')}</h2>
      <p className="text-sm text-muted">{t('apiAgents.security')}</p>
      <pre className="overflow-auto rounded bg-bg p-3 text-xs font-mono">{`OTLP HTTP: ${window.location.origin}/v1/{logs,traces,metrics}\nX-Api-Key: <INGESTION_KEY>\nOTLP gRPC: X-Api-Key metadata; use your configured HTTP/2 listener`}</pre>
      <Select value={client} onValueChange={setClient} options={[{ value: 'claude', label: 'Claude Code' }, { value: 'cursor', label: 'Cursor' }, { value: 'vscode', label: 'VS Code' }, { value: 'codex', label: 'Codex CLI' }, { value: 'generic', label: t('apiAgents.generic') }]} />
      <pre className="overflow-auto rounded bg-bg p-3 text-xs font-mono">{configuration}</pre>
      <Button variant="link" icon={<Copy className="size-4" />} onClick={() => void copy(configuration)}>{t('apiAgents.copyConfig')}</Button>
      <p className="text-sm text-muted">{t('apiAgents.tools')}</p>
      <div className="grid gap-3 md:grid-cols-2" data-testid="agent-tool-groups">
        {[
          ['discovery', ['list_data_sources', 'get_data_schema', 'list_tables', 'describe_table', 'get_data_organization', 'get_field_values']],
          ['query', ['query_data', 'aggregate_data', 'compare_periods']],
          ['observability', ['list_services', 'search_logs', 'search_traces', 'get_trace', 'summarize_logs', 'summarize_traces', 'summarize_errors', 'get_service_dependencies']],
          ['metrics', ['list_metric_names', 'list_metric_labels', 'query_metric_series']],
          ['iot', ['list_devices', 'list_device_signals', 'query_iot_series']],
          ['rum', ['list_rum_projects', 'summarize_rum', 'summarize_rum_errors']],
        ].map(([group, tools]) => <div key={String(group)} className="space-y-1 rounded border border-border p-3">
          <h3 className="text-sm font-medium">{t(`apiAgents.groups.${group}`)}</h3>
          <p className="text-xs text-muted">{t(`apiAgents.groupHints.${group}`)}</p>
          <p className="break-words text-xs font-mono text-muted">{(tools as string[]).join(' · ')}</p>
        </div>)}
      </div>
      <p className="text-sm text-muted">{t('apiAgents.workflow')}</p>
    </section>
    <Modal open={kind !== null} onOpenChange={open => { if (!open) setKind(null); }} title={t('apiAgents.create')} footer={<Button loading={pending} onClick={() => void handleSubmit(values => kind && run({ kind, action: 'create', name: values.name.trim() }))()}>{t('apiAgents.create')}</Button>}>
      <form onSubmit={handleSubmit(values => { if (kind) void run({ kind, action: 'create', name: values.name.trim() }); })}><Field required label={t('apiAgents.name')} error={errors.name?.message}><Input {...register('name', { required: t('apiAgents.nameRequired'), maxLength: 64, validate: value => !!value.trim() || t('apiAgents.nameRequired') })} /></Field></form>
    </Modal>
    <Modal open={secretOpen} onOpenChange={open => { if (!open) closeSecret(); }} title={t('apiAgents.secretTitle')} footer={<Button disabled={!secret} icon={<Copy className="size-4" />} onClick={() => void copy(secret)}>{t('apiAgents.copyKey')}</Button>}>
      <p className="mb-3 text-sm text-muted">{t('apiAgents.secretHint')}</p>{secret ? <pre className="break-all whitespace-pre-wrap rounded bg-bg p-3 font-mono text-sm">{secret}</pre> : <p role="status" className="text-sm text-muted">{t('common.loading')}</p>}
    </Modal>
  </div>;
}
