import { useTranslation } from 'react-i18next';
import { Copy, FileJson, RefreshCw } from 'lucide-react';
import type { StartupSettings } from '@/api/types';
import { Button, toast } from '@/design-system';

/** configs/system.json 覆盖模板（仅功能/部署项；内存、并发、缓冲等性能参数由系统按环境自动调控，重启生效）. */
const TEMPLATE = JSON.stringify(
  {
    SData: {
      Port: 5300,
      WebUI: true,
      Mqtt: { Port: 1883 },
      Database: { Path: 'data/sdata.db' },
      DuckDb: {
        Path: 'data/sdata.duckdb',
        TempDirectory: '',
      },
      Ingest: {
        RequireAuth: true,
      },
    },
  },
  null,
  2,
);

/** 启动配置页签：说明 configs/system.json 覆盖机制 + 当前生效值（只读快照）. */
export function StartupConfigPanel({ startup, onRetry }: { startup: StartupSettings | null | undefined; onRetry: () => void }) {
  const { t } = useTranslation();

  if (!startup) {
    return (
      <section className="rounded-lg border border-border bg-surface p-4" data-testid="startup-unavailable">
        <h2 className="text-sm font-semibold text-fg">{t('settings.startupTitle')}</h2>
        <p role="alert" className="mt-1 text-sm text-muted">{t('settings.startupUnavailable')}</p>
        <Button className="mt-3" variant="link" size="sm" icon={<RefreshCw className="size-3.5" />} onClick={onRetry}>{t('settings.retry')}</Button>
      </section>
    );
  }

  const copy = () => {
    void navigator.clipboard.writeText(TEMPLATE).then(() => toast.success(t('settings.startupCopied'))).catch(() => toast.error(t('settings.startupCopyFailed')));
  };
  const rows: Array<[string, string]> = [
    [t('settings.startupPort'), String(startup.port)],
    [t('settings.startupMqttPort'), String(startup.mqttPort)],
    [t('settings.startupWebUI'), startup.webUi ? t('settings.startupOn') : t('settings.startupOff')],
    [t('settings.startupDatabasePath'), startup.databasePath],
    [t('settings.startupDuckDbPath'), startup.duckDbPath],
    [t('settings.startupTempDirectory'), startup.duckDbTempDirectory?.trim() || t('settings.startupSystemDefault')],
    [t('settings.startupIngestRequireAuth'), startup.ingestRequireAuth ? t('settings.startupOn') : t('settings.startupOff')],
  ];

  return (
    <div className="flex flex-col gap-3">
      <section className="rounded-lg border border-border bg-surface p-4" data-testid="startup-settings">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-fg">{t('settings.startupTitle')}</h2>
          <span className={`inline-flex items-center gap-1 rounded px-2 py-0.5 text-xs ${startup.systemJsonExists ? 'bg-surface-2 text-fg' : 'text-subtle'}`}>
            <FileJson className="size-3.5" aria-hidden />
            {startup.systemJsonExists ? t('settings.startupLoaded') : t('settings.startupNotCreated')}
          </span>
        </div>
        <p className="mt-1 text-xs leading-relaxed text-subtle">{t('settings.startupHint')}</p>
        <p className="mt-1 text-xs leading-relaxed text-subtle">{t('settings.startupPrecedence')}</p>
        <pre data-testid="startup-template" className="mt-3 max-h-80 overflow-auto rounded border border-border bg-surface-2 p-3 font-mono text-xs">{TEMPLATE}</pre>
        <div className="mt-3">
          <Button size="sm" icon={<Copy className="size-3.5" />} onClick={copy}>{t('settings.startupCopyTemplate')}</Button>
        </div>
      </section>
      <section className="rounded-lg border border-border bg-surface p-4">
        <h2 className="text-sm font-semibold text-fg">{t('settings.startupValuesTitle')}</h2>
        <dl className="mt-3 grid grid-cols-[minmax(10rem,auto)_1fr] gap-x-4 gap-y-1.5 text-xs">
          {rows.map(([label, value]) => (
            <div key={label} className="col-span-2 grid grid-cols-subgrid items-baseline">
              <dt className="text-muted">{label}</dt>
              <dd className="break-all font-mono text-fg">{value}</dd>
            </div>
          ))}
        </dl>
      </section>
    </div>
  );
}
