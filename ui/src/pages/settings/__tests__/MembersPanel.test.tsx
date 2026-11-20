import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import '@/i18n';
import { MembersPanel } from '../MembersPanel';
import { useAuthStore } from '@/store/auth';

// mock 用户管理 API：返回一个 admin + 一个只读用户
vi.mock('@/api/users', () => ({
  fetchUsers: vi.fn().mockResolvedValue({
    page: 1,
    pageSize: 20,
    count: 2,
    data: [
      { id: 1, username: 'admin', role: 'admin', createTime: '2026-01-01T00:00:00Z' },
      { id: 2, username: 'viewer', role: 'readonly', createTime: '2026-01-02T00:00:00Z' },
    ],
  }),
  createUser: vi.fn(),
  deleteUser: vi.fn(),
  resetUserPassword: vi.fn(),
  updateUserRole: vi.fn(),
}));

// mock 改密 API（MembersPanel 引入 changePassword）
vi.mock('@/api/auth', () => ({
  changePassword: vi.fn(),
}));

function renderPanel() {
  return render(<MembersPanel />);
}

describe('MembersPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
  });

  it('渲染成员列表并展示角色徽章', async () => {
    renderPanel();
    expect(await screen.findByText('admin')).toBeInTheDocument();
    expect(screen.getByText('viewer')).toBeInTheDocument();
    expect(screen.getByText('超级管理员')).toBeInTheDocument();
    expect(screen.getByText('只读')).toBeInTheDocument();
  });

  it('admin 行不展示删除与改权限按钮，只读用户展示', async () => {
    renderPanel();
    await screen.findByText('viewer');
    // 全局只有一个"改权限"按钮（对应 viewer 行）
    expect(screen.getAllByRole('button', { name: /改权限/ })).toHaveLength(1);
    // admin 行无删除：删除按钮只出现一次（viewer 行）
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1);
  });

  it('管理员可见「新建用户」入口', async () => {
    renderPanel();
    expect(await screen.findByRole('button', { name: /新建用户/ })).toBeInTheDocument();
  });

  it('只读成员仅能修改自己密码，无任何管理按钮', async () => {
    useAuthStore.setState({ token: 'test-token', username: 'viewer', role: 'readonly' });
    renderPanel();
    await screen.findByText('viewer');
    // 自己行有「修改密码」，全局仅此一个操作按钮
    expect(screen.getAllByRole('button', { name: /修改密码/ })).toHaveLength(1);
    expect(screen.queryByRole('button', { name: /新建用户/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /重置密码/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /改权限/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument();
  });
});
