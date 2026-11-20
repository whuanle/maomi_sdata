import dayjs from 'dayjs';

/** 时间显示格式（YYYY-MM-DD HH:mm:ss）. */
export function formatDateTime(value: string | number | Date | undefined | null): string {
  if (!value) {
    return '-';
  }

  return dayjs(value).format('YYYY-MM-DD HH:mm:ss');
}

/** 毫秒级时间显示（YYYY-MM-DD HH:mm:ss.SSS，HyperDX 式日志时间戳精度）. */
export function formatDateTimeMs(value: string | number | Date | undefined | null): string {
  if (!value) {
    return '-';
  }

  return dayjs(value).format('YYYY-MM-DD HH:mm:ss.SSS');
}

/** 相对时间短格式（"25s ago"/"3m ago"，语言中性，HyperDX 式详情头部展示）. */
export function formatRelativeTime(value: string | number | Date | undefined | null, now: number = Date.now()): string {
  if (!value) {
    return '';
  }

  const ts = dayjs(value).valueOf();
  if (!Number.isFinite(ts)) {
    return '';
  }

  const diffSeconds = Math.max(0, Math.round((now - ts) / 1000));
  if (diffSeconds < 60) {
    return `${diffSeconds}s ago`;
  }

  const diffMinutes = Math.round(diffSeconds / 60);
  if (diffMinutes < 60) {
    return `${diffMinutes}m ago`;
  }

  const diffHours = Math.round(diffMinutes / 60);
  if (diffHours < 24) {
    return `${diffHours}h ago`;
  }

  return `${Math.round(diffHours / 24)}d ago`;
}

/** 微秒耗时展示. */
export function formatDurationUs(durationUs: number): string {
  if (!Number.isFinite(durationUs) || durationUs < 0) return '-';
  if (durationUs >= 999_950) {
    return `${(durationUs / 1_000_000).toFixed(3)} s`;
  }

  if (durationUs >= 1_000) {
    return `${(durationUs / 1_000).toFixed(1)} ms`;
  }

  return `${durationUs} µs`;
}

/** 字节数展示. */
export function formatBytes(bytes: number): string {
  if (bytes >= 1024 ** 3) {
    return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
  }

  if (bytes >= 1024 ** 2) {
    return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  }

  if (bytes >= 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${bytes} B`;
}

/** 速率保留有限小数，极小正值用精度下限表示. */
export function formatRate(value: number, precision = 2): string {
  if (!Number.isFinite(value) || value < 0) return '—';
  const minimum = 10 ** -precision;
  if (value > 0 && value < minimum) return `<${minimum}`;
  return String(Number(value.toFixed(precision)));
}

/** 字节速率，不改变容量值的显示规则. */
export function formatBytesRate(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  const index = Math.min(bytes > 0 ? Math.floor(Math.log(bytes) / Math.log(1024)) : 0, units.length - 1);
  const unit = Math.max(0, index);
  return `${formatRate(bytes / 1024 ** unit, 1)} ${units[unit]}/s`;
}

/** 运行时长. */
export function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) {
    return `${days}d ${hours}h`;
  }

  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }

  return `${minutes}m`;
}
