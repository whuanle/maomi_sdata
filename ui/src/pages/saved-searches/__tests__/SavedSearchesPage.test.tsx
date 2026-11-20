import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import '@/i18n';
import { SavedSearchesPage } from '../SavedSearchesPage';
import { listSavedQueries } from '@/api/analytics';
import { useAuthStore } from '@/store/auth';

vi.mock('@/api/analytics', () => ({
  listSavedQueries: vi.fn().mockResolvedValue({ page: 1, pageSize: 100, count: 2, data: [
    { id: 2, name: '错误日志', kind: 'search', sqlText: '', definition: '{"domain":"logs","mode":"lucene","where":"severity:ERROR","fields":"ts, service","orderBy":"ts DESC","limit":500,"rangeMs":900000}', description: '', createTime: '2026-11-16T10:00:00Z', updateTime: '2026-11-16T11:00:00Z' },
    { id: 3, name: '慢链路', kind: 'search', sqlText: '', definition: '{"domain":"traces","sql":"SELECT ts FROM {{traces}} WHERE ts >= $start","rangeMs":900000}', description: '', createTime: '2026-11-16T10:00:00Z', updateTime: '2026-11-16T12:00:00Z' },
  ] }),
  deleteSavedQuery: vi.fn().mockResolvedValue({}),
}));

describe('SavedSearchesPage', () => {
  beforeEach(() => {
    useAuthStore.setState({ token: 'test-token', username: 'admin', role: 'admin' });
  });

  it('渲染已保存搜索卡片：名称/数据源/快照预览/更新时间 + 打开与删除操作', async () => {
    render(
      <MemoryRouter>
        <SavedSearchesPage />
      </MemoryRouter>,
    );
    expect(await screen.findByText('错误日志')).toBeTruthy();
    expect(screen.getByText('慢链路')).toBeTruthy();
    // 数据源标签（logs=日志 / traces=链路）
    expect(screen.getByText('日志')).toBeTruthy();
    expect(screen.getByText('链路')).toBeTruthy();
    // 快照预览（Lucene 表达式 / SQL 首行）
    expect(screen.getByText('severity:ERROR')).toBeTruthy();
    expect(screen.getByText('SELECT ts FROM {{traces}} WHERE ts >= $start')).toBeTruthy();
    // 操作按钮（admin 角色可见删除）+ 新建搜索入口
    expect(screen.getAllByText('打开').length).toBe(2);
    expect(screen.getAllByText('删除').length).toBe(2);
    expect(screen.getByText('新建搜索')).toBeTruthy();
    expect(screen.getByTestId('saved-searches-filter')).toBeTruthy();
    expect(listSavedQueries).toHaveBeenCalledWith(expect.objectContaining({ kind: 'search' }));
  });

  it('名称筛选框与新建搜索按钮在同一行左对齐', async () => {
    render(
      <MemoryRouter>
        <SavedSearchesPage />
      </MemoryRouter>,
    );
    await screen.findByText('错误日志');
    const filter = screen.getByTestId('saved-searches-filter');
    const toolbar = filter.parentElement!;
    expect(toolbar.classList.contains('w-full')).toBe(true);
    expect(toolbar.classList.contains('flex')).toBe(true);
    expect(toolbar.classList.contains('justify-end')).toBe(false);
    expect(toolbar.classList.contains('justify-between')).toBe(false);
    expect(toolbar.contains(screen.getByRole('button', { name: '新建搜索' }))).toBe(true);
    expect(toolbar.firstElementChild).toBe(filter);
  });

  it('按名称筛选：命中保留、未命中显示空态', async () => {
    render(
      <MemoryRouter>
        <SavedSearchesPage />
      </MemoryRouter>,
    );
    await screen.findByText('错误日志');
    fireEvent.change(screen.getByTestId('saved-searches-filter'), { target: { value: '慢' } });
    await waitFor(() => {
      expect(screen.queryByText('错误日志')).toBeNull();
    });
    expect(screen.getByText('慢链路')).toBeTruthy();
    // 筛选无命中时显示空态提示
    fireEvent.change(screen.getByTestId('saved-searches-filter'), { target: { value: '不存在' } });
    await waitFor(() => {
      expect(screen.getByText('暂无数据')).toBeTruthy();
    });
  });
});
