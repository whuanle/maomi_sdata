import { act, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import i18n from '@/i18n';
import { AppSider } from '../AppSider';

describe('AppSider', () => {
  it('按界面语言显示仪表盘与客户端会话菜单', async () => {
    const previousLanguage = i18n.language;
    try {
      await i18n.changeLanguage('zh-CN');
      render(<MemoryRouter><AppSider /></MemoryRouter>);

      const navigation = within(screen.getByRole('navigation', { name: 'SData' }));
      expect(navigation.getByRole('button', { name: '仪表盘' })).toBeInTheDocument();
      expect(navigation.getByRole('button', { name: '客户端会话' })).toBeInTheDocument();
      expect(navigation.queryByRole('button', { name: 'Dashboards' })).not.toBeInTheDocument();
      expect(navigation.queryByRole('button', { name: /^Client Sessions?$/ })).not.toBeInTheDocument();

      await act(async () => { await i18n.changeLanguage('en-US'); });
      expect(navigation.getByRole('button', { name: 'Dashboards' })).toBeInTheDocument();
      expect(navigation.getByRole('button', { name: 'Client Sessions' })).toBeInTheDocument();
      expect(navigation.queryByRole('button', { name: '仪表盘' })).not.toBeInTheDocument();
      expect(navigation.queryByRole('button', { name: '客户端会话' })).not.toBeInTheDocument();
    } finally {
      await act(async () => { await i18n.changeLanguage(previousLanguage); });
    }
  });

  it('优先显示总览、搜索、已保存搜索、图表与仪表盘，其余菜单保持原序', () => {
    render(<MemoryRouter><AppSider /></MemoryRouter>);

    const items = within(screen.getByRole('navigation', { name: 'SData' })).getAllByRole('button');
    expect(items.map((item) => item.textContent)).toEqual([
      'overview', 'search', 'savedSearches', 'charts', 'serviceMap', 'dashboards',
      'rum', 'iot', 'analytics', 'logConsumption', 'settings',
    ].map((key) => i18n.t(`menu.${key}`)));
  });
});
