import { create } from 'zustand';
import { persist } from 'zustand/middleware';

/** 系统角色：超级管理员 / 可写 / 只读（不开放注册，超管由系统种子唯一提供）. */
export type SDataRole = 'admin' | 'write' | 'readonly';

/** 兼容旧版角色：'user'（可写）统一归一为 'write'. */
export function normalizeRole(role: string | null | undefined): SDataRole {
  if (role === 'admin') return 'admin';
  if (role === 'readonly') return 'readonly';
  return 'write';
}

/** 是否超级管理员. */
export function isAdminRole(role: string | null | undefined): boolean {
  return normalizeRole(role) === 'admin';
}

/** 是否可写（超级管理员或可写用户，只读用户仅查询）. */
export function canWriteRole(role: string | null | undefined): boolean {
  return normalizeRole(role) !== 'readonly';
}

/** 登录态. */
export interface AuthState {
  token: string | null;
  username: string | null;
  role: SDataRole | null;
  setAuth: (token: string, username: string, role: string) => void;
  clear: () => void;
}

/** 认证 store（持久化到 localStorage）. */
export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      token: null,
      username: null,
      role: null,
      setAuth: (token, username, role) => set({ token, username, role: normalizeRole(role) }),
      clear: () => set({ token: null, username: null, role: null }),
    }),
    {
      name: 'sdata-auth',
      version: 1,
      // v0 存的是旧角色字符串（admin/user），迁移时把 'user' 归一为 'write'
      migrate: (persisted) => {
        const state = persisted as Partial<AuthState>;
        return { ...state, role: normalizeRole(state.role) } as AuthState;
      },
    },
  ),
);
