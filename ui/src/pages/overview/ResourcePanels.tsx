import { useTranslation } from 'react-i18next';
import type { ResourceSnapshot, LocalStorageSnapshot, S3StorageSnapshot } from '@/api/types';
import { formatBytes, formatDateTime } from '@/utils/format';

export function ResourcePanels({ resources, local, s3 }: { resources?: ResourceSnapshot; local?: LocalStorageSnapshot; s3?: S3StorageSnapshot }) {
  const { t } = useTranslation();
  const unknown = t('overview.unknown');
  const bytes = (value?: number | null) => value == null ? unknown : formatBytes(value);
  const percent = (value?: number | null) => value == null ? unknown : `${value.toFixed(1)}%`;
  const used = (total?: number | null, free?: number | null) => total == null || free == null ? null : total - free;
  const states = ['ok', 'partial', 'error', 'unsupported', 'unknown', 'notConfigured', 'checking', 'timeout', 'forbidden'];
  const state = (value?: string) => t(`overview.resourceState.${states.includes(value ?? '') ? value : 'unknown'}`);
  const row = (key: string, value: React.ReactNode) => <div key={key} className="flex flex-wrap justify-between gap-2 border-b border-border/60 py-2 text-xs"><span className="text-muted">{t(`overview.resource.${key}`)}</span><span className="break-all text-right font-mono">{value}</span></div>;
  const stamp = (value?: string | null, status?: string) => <p className="my-2 text-xs text-muted">{state(status)} · {value ? formatDateTime(value) : unknown}</p>;
  const panel = (key: string, children: React.ReactNode) => <section className="min-w-0 rounded-lg border border-border bg-surface p-4"><h3 className="text-sm font-medium">{t(`overview.resource.${key}`)}</h3>{children}</section>;
  return <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
    {panel('host', <>
      {stamp(resources?.sampledAt, resources?.state)}
      {row('architecture', resources ? `${resources.osArchitecture} / ${resources.processArchitecture}` : unknown)}
      {row('runtime', resources?.runtime ?? unknown)}
      {row('cores', resources?.processorCount ?? unknown)}
      {row('hostCpu', percent(resources?.systemCpuPercent))}
      {row('memoryTotal', bytes(resources?.totalMemoryBytes))}
      {row('memoryAvailable', bytes(resources?.availableMemoryBytes))}
      {row('memoryUsed', bytes(used(resources?.totalMemoryBytes, resources?.availableMemoryBytes)))}
      {row('memoryPercent', resources?.totalMemoryBytes && resources.availableMemoryBytes != null ? percent((1 - resources.availableMemoryBytes / resources.totalMemoryBytes) * 100) : unknown)}
      {row('containerLimit', bytes(resources?.containerMemoryLimitBytes))}
      {row('containerUsed', bytes(resources?.containerMemoryBytes))}
      {row('containerCpu', resources?.containerCpuLimit ?? unknown)}
      <p className="mt-3 text-xs text-subtle">{t('overview.resource.hostHint')}</p>
    </>)}
    {panel('process', <>
      {stamp(resources?.sampledAt, resources?.state)}
      {row('processCpu', percent(resources?.processCpuPercent))}
      {row('workingSet', bytes(resources?.workingSetBytes))}
      {row('privateMemory', bytes(resources?.privateMemoryBytes))}
      {row('gc', bytes(resources?.gcHeapBytes))}
      {row('threads', resources?.processThreads ?? unknown)}
      {row('pool', resources?.threadPoolThreads ?? unknown)}
      {row('pending', resources?.pendingWorkItems ?? unknown)}
      <p className="mt-3 text-xs text-subtle">{t('overview.resource.cpuHint')}</p>
    </>)}
    {panel('duckdb', <>
      {stamp(local?.sampledAt, local?.state)}
      {row('queryState', state(local?.duckDbState))}
      {stamp(local?.duckDbSampledAt, local?.duckDbState)}
      {row('tracked', bytes(local?.trackedMemoryBytes))}
      {row('spill', bytes(local?.temporaryBytes))}
      {row('limit', local?.memoryLimit ?? unknown)}
      {row('duckThreads', local?.threads ?? unknown)}
      {row('fileState', state(local?.fileState))}
      {stamp(local?.fileSampledAt, local?.fileState)}
      {row('database', bytes(local?.databaseBytes))}
      {row('wal', bytes(local?.walBytes))}
      {(local?.disks ?? []).map((disk) => <div key={disk.volume} className="mt-3 rounded border border-border p-2">
        <div className="break-all font-mono text-xs">{disk.volume} · {state(disk.state)}</div>
        {row('diskTotal', bytes(disk.totalBytes))}
        {row('diskAvailable', bytes(disk.availableBytes))}
        {row('diskUsed', bytes(used(disk.totalBytes, disk.availableBytes)))}
        {row('diskPercent', disk.totalBytes && disk.availableBytes != null ? percent((1 - disk.availableBytes / disk.totalBytes) * 100) : unknown)}
      </div>)}
      <p className="mt-3 text-xs text-subtle">{t('overview.resource.duckHint')}</p>
    </>)}
    {panel('s3', <>
      {stamp(s3?.sampledAt, s3?.state)}
      {row('checked', s3?.checkedAt ? formatDateTime(s3.checkedAt) : unknown)}
      {row('scope', s3?.bucket ? `${s3.bucket}/${s3.prefix}` : unknown)}
      {row('s3Bytes', bytes(s3?.bytes))}
      {row('objects', s3?.objects ?? unknown)}
      <p className="mt-3 text-xs text-subtle">{t('overview.resource.s3Hint')}</p>
    </>)}
  </div>;
}
