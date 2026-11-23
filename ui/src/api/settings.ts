import { del, get, post, put } from './client';
import type { MqttAuthSettings, SaveMqttAuthSettings, NotificationChannel, SaveNotificationChannel, NotificationSendResult } from './types';
import type {
  CompressionSettings,
  MqInstance,
  MqParsePreviewRequest,
  MqParsePreviewResult,
  OtlpSettings,
  SaveOtlpSettings,
  RetentionSettings,
  S3Settings,
  SettingsResponse,
  TestS3ConnectionResponse,
} from './types';

export const fetchMqttAuthSettings = () => get<MqttAuthSettings>('/settings/mqtt-auth');
export const saveMqttAuthSettings = (body: SaveMqttAuthSettings) => post<MqttAuthSettings>('/settings/mqtt-auth', body);

export const fetchNotificationChannels = () => get<NotificationChannel[]>('/settings/notification-channels');
export const saveNotificationChannel = (body: SaveNotificationChannel) => put<NotificationChannel>('/settings/notification-channels', body);
export const deleteNotificationChannel = (id: string, version: number) => del<boolean>(`/settings/notification-channels/${encodeURIComponent(id)}?version=${version}`);
export const testNotificationChannel = (body: SaveNotificationChannel) => post<NotificationSendResult>('/settings/notification-channels/test', body);

/** SecretKey 掩码（GET 返回；提交该值或空 = 沿用已存密钥）. */
export const S3_SECRET_MASK = '******';

/** 系统设置（管理员；SecretKey 掩码返回）. */
export function fetchSettings() {
  return get<SettingsResponse>('/settings');
}

/** 保存 S3 冷存储设置（立即重建 SECRET，立即生效）. */
export function saveS3Settings(body: Partial<S3Settings>) {
  return put<S3Settings>('/settings/s3', body);
}

/** S3 连接测试（SecretKey 空/掩码 = 沿用已存值）. */
export function testS3Connection(body: Partial<S3Settings>) {
  return post<TestS3ConnectionResponse>('/settings/s3/test', body);
}

/** 保存三段保留设置（下一轮保留/归档 tick 生效）. */
export function saveRetentionSettings(body: RetentionSettings) {
  return put<RetentionSettings>('/settings/retention', { retention: body });
}

/** 保存数据压缩设置（下一轮归档 tick 生效）. */
export function saveCompressionSettings(body: CompressionSettings) {
  return put<CompressionSettings>('/settings/compression', body);
}

/** MQ 日志消费实例列表（多实例，含每 topic 解析规则）. */
export function fetchMqInstances() {
  return get<MqInstance[]>('/settings/mq-instances');
}

/** 保存 MQ 日志消费实例列表（全量替换；保存后由消费管理器自动热更新，无需重启）. */
export function saveMqInstances(instances: MqInstance[]) {
  return put<void>('/settings/mq-instances', { instances });
}

/** MQ 日志解析预览（样例文本按解析规则试解析，不落库）. */
export function parseMqPreview(body: MqParsePreviewRequest) {
  return post<MqParsePreviewResult>('/settings/mq-instances/parse-preview', body);
}

/** 保存自身遥测导出设置（重启生效）. */
export function saveOtlpSettings(body: SaveOtlpSettings) {
  return put<OtlpSettings>('/settings/otlp', body);
}
