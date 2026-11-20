import { del, get, post } from './client';
import type { PagedQuery, PagedResult, UserItem } from './types';

/** 用户列表（仅超级管理员）. */
export function fetchUsers(query: PagedQuery) {
  return get<PagedResult<UserItem>>('/users', query);
}

/** 创建用户（角色仅 write / readonly）. */
export function createUser(username: string, password: string, role: string) {
  return post<number>('/users', { username, password, role });
}

/** 删除用户. */
export function deleteUser(id: number) {
  return del<unknown>(`/users/${id}`);
}

/** 重置用户密码. */
export function resetUserPassword(id: number, newPassword: string) {
  return post<unknown>(`/users/${id}/password/reset`, { newPassword });
}

/** 修改用户权限（write / readonly）. */
export function updateUserRole(id: number, role: string) {
  return post<unknown>(`/users/${id}/role`, { role });
}
