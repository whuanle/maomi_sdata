import { get, post } from './client';
import type { CurrentUser, LoginResponse } from './types';

/** 登录. */
export function login(username: string, password: string) {
  return post<LoginResponse>('/auth/login', { username, password });
}

/** 当前用户. */
export function fetchCurrentUser() {
  return get<CurrentUser>('/auth/me');
}

/** 修改当前用户密码（验证旧密码）. */
export function changePassword(oldPassword: string, newPassword: string) {
  return post<unknown>('/auth/password/change', { oldPassword, newPassword });
}
