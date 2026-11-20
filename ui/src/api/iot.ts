import { del, get, post } from './client';
import type { DeviceItem, DeviceLatestValue, PagedQuery, PagedResult, TelemetrySeriesItem } from './types';

/** 设备列表参数. */
export interface DeviceQueryParams extends PagedQuery {
  search?: string;
  group?: string;
}

/** 设备列表. */
export function queryDevices(params: DeviceQueryParams) {
  return get<PagedResult<DeviceItem>>('/iot/devices', params);
}

/** 保存设备. */
export function saveDevice(device: Partial<DeviceItem>) {
  return post<{ value: number }>('/iot/devices', device);
}

/** 删除设备. */
export function deleteDevice(id: number) {
  return del<unknown>(`/iot/devices/${id}`);
}

/** 遥测查询参数. */
export interface TelemetryQueryParams {
  deviceKey: string;
  signals?: string[];
  start?: string;
  end?: string;
  aggregation?: string;
  stepSeconds?: number;
}

/** 遥测查询. */
export function queryTelemetry(params: TelemetryQueryParams) {
  return get<{ series: TelemetrySeriesItem[] }>('/iot/devices/telemetry', params);
}

/** 设备最新值. */
export function getDeviceLatest(deviceKey: string) {
  return get<DeviceLatestValue[]>(`/iot/devices/${deviceKey}/latest`);
}
