import { get, post, del } from './client';
import type { AlertChannelItem, AlertDraft, AlertHistory, AlertItem, AlertPoint } from './types';

export const listAlerts = (savedQueryId?: string) => get<AlertItem[]>(`/alerts${savedQueryId ? `?savedQueryId=${encodeURIComponent(savedQueryId)}` : ''}`);
export const saveAlert = (draft: AlertDraft) => post<AlertItem>('/alerts', draft);
export const deleteAlert = (id: string, version: number) => del<boolean>(`/alerts/${id}?version=${version}`);
export const alertHistory = (id: string) => get<AlertHistory>(`/alerts/${id}/history`);
export const alertChannels = () => get<AlertChannelItem[]>('/alerts/channels');
export const previewAlert = (draft: Pick<AlertDraft, 'domain' | 'condition' | 'parameters' | 'windowMinutes'>) => post<AlertPoint>('/alerts/preview', draft);
