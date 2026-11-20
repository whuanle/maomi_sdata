import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import i18n from '@/i18n';
import { ResourcePanels } from '../ResourcePanels';

describe('ResourcePanels', () => {
  beforeEach(async () => { await i18n.changeLanguage('zh-CN'); });

  it('展示系统与进程资源、磁盘和 S3 实际用量', () => {
    render(<ResourcePanels resources={{
      state: 'ok', sampledAt: '2026-11-18T00:00:00Z', os: 'Windows',
      osArchitecture: 'X64', processArchitecture: 'X64', runtime: '.NET 10',
      processorCount: 8, systemCpuPercent: 12.5, processCpuPercent: 3.2,
      totalMemoryBytes: 8 * 1024 ** 3, availableMemoryBytes: 2 * 1024 ** 3,
      workingSetBytes: 200 * 1024 ** 2, privateMemoryBytes: 150 * 1024 ** 2,
      processThreads: 37, threadPoolThreads: 11, pendingWorkItems: 2,
    }} local={{
      state: 'ok', duckDbState: 'ok', fileState: 'ok', trackedMemoryBytes: 30 * 1024 ** 2,
      databaseBytes: 50 * 1024 ** 2, walBytes: 1024, temporaryBytes: 0, threads: 4, memoryLimit: '512MB',
      disks: [{ volume: 'F:/', state: 'ok', totalBytes: 100 * 1024 ** 3, availableBytes: 25 * 1024 ** 3 }],
    }} s3={{ state: 'ok', bucket: 'archive', prefix: 'sdata/', bytes: 60 * 1024 ** 2, objects: 25 }} />);
    expect(screen.getByText('12.5%')).toBeInTheDocument();
    expect(screen.getByText('3.2%')).toBeInTheDocument();
    expect(screen.getAllByText('75.0%')).toHaveLength(2);
    expect(screen.getByText('200.0 MB')).toBeInTheDocument();
    expect(screen.getByText('30.0 MB')).toBeInTheDocument();
    expect(screen.getByText('60.0 MB')).toBeInTheDocument();
    expect(screen.getByText('archive/sdata/')).toBeInTheDocument();
    expect(screen.getByText('37')).toBeInTheDocument();
  });

  it('缺失值不显示为零，失败值注明历史快照', () => {
    render(<ResourcePanels s3={{ state: 'forbidden', bucket: 'archive', prefix: '', bytes: 1024, objects: 1, sampledAt: '2026-11-18T00:00:00Z' }} />);
    expect(screen.getAllByText('未知').length).toBeGreaterThan(0);
    expect(screen.getByText(/无列举权限 \/ 历史快照/)).toBeInTheDocument();
    expect(screen.getByText('1.0 KB')).toBeInTheDocument();
    expect(screen.queryByText('0.0%')).not.toBeInTheDocument();
  });
});
