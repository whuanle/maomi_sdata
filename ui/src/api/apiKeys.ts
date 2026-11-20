import { get, post } from './client';
import type { ApiKeyItem, ApiKeySecretResponse, ManageApiKey } from './types';

export const fetchApiKeys = () => get<ApiKeyItem[]>('/settings/api-keys');
export const manageApiKey = (command: ManageApiKey) => post<ApiKeySecretResponse>('/settings/api-keys', command);
