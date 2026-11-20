import type { ReactNode } from 'react';
import { Navigate, Outlet } from 'react-router';
import { isAdminRole, useAuthStore } from '@/store/auth';

interface RequireAuthProps {
  children?: ReactNode;
  /** 允许访问的角色（缺省仅要求登录；['admin'] 即仅超级管理员）. */
  roles?: string[];
}

/** 登录守卫：未登录跳转登录页；声明 roles 时非目标角色跳回概览页. */
export function RequireAuth({ children, roles }: RequireAuthProps) {
  const token = useAuthStore((state) => state.token);
  const role = useAuthStore((state) => state.role);
  if (!token) {
    return <Navigate to="/login" replace />;
  }

  if (roles && !roles.some((item) => (item === 'admin' ? isAdminRole(role) : item === role))) {
    return <Navigate to="/overview" replace />;
  }

  return children ?? <Outlet />;
}
