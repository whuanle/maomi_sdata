import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { NotificationChannelsPanel } from '../NotificationChannelsPanel';

const { fetchChannels, saveChannel, testChannel } = vi.hoisted(() => ({
  fetchChannels: vi.fn(), saveChannel: vi.fn(), testChannel: vi.fn(),
}));
vi.mock('@/api/settings', () => ({
  fetchNotificationChannels: fetchChannels,
  saveNotificationChannel: saveChannel,
  testNotificationChannel: testChannel,
  deleteNotificationChannel: vi.fn(),
}));

beforeEach(() => {
  vi.clearAllMocks();
  fetchChannels.mockResolvedValue([]);
  saveChannel.mockResolvedValue({});
  testChannel.mockResolvedValue({ success: true, message: 'ok' });
});

describe('NotificationChannelsPanel', () => {
  it('renders all eleven types and opens an isolated draft', async () => {
    render(<NotificationChannelsPanel />);
    expect(await screen.findByText('Telegram Bot')).toBeTruthy();
    expect(screen.getByText('Custom JSON Webhook')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /^新建实例$|^New instance$/ })).toHaveLength(11);
    fireEvent.click(screen.getAllByRole('button', { name: /^新建实例$|^New instance$/ })[0]);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Chat ID')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'close' }));
    expect(saveChannel).not.toHaveBeenCalled();
    expect(testChannel).not.toHaveBeenCalled();
  });

  it('saves a Telegram instance without sending a test automatically', async () => {
    render(<NotificationChannelsPanel />);
    fireEvent.click((await screen.findAllByRole('button', { name: /^新建实例$|^New instance$/ }))[0]);
    const dialog = await screen.findByRole('dialog');
    const inputs = dialog.querySelectorAll('input');
    fireEvent.change(inputs[0], { target: { value: 'Operations' } });
    fireEvent.change(inputs[1], { target: { value: '123:fake' } });
    fireEvent.change(inputs[2], { target: { value: '456' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /^保存$|^Save$/ }));
    await waitFor(() => expect(saveChannel).toHaveBeenCalledWith({
      version: 0, name: 'Operations', type: 'telegram', enabled: true,
      config: { token: '123:fake', chatId: '456' }, keepSecrets: [],
    }));
    expect(testChannel).not.toHaveBeenCalled();
  });

  it('requires confirmation before sending a real test request', async () => {
    render(<NotificationChannelsPanel />);
    fireEvent.click((await screen.findAllByRole('button', { name: /^新建实例$|^New instance$/ }))[0]);
    const dialog = await screen.findByRole('dialog');
    const inputs = dialog.querySelectorAll('input');
    fireEvent.change(inputs[0], { target: { value: 'Operations' } });
    fireEvent.change(inputs[1], { target: { value: '123:fake' } });
    fireEvent.change(inputs[2], { target: { value: '456' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /^测试发送$|^Send test$/ }));
    expect(testChannel).not.toHaveBeenCalled();
    expect(await screen.findByText(/此操作将向目标发送一条真实测试通知|This will send a real test notification/)).toBeTruthy();
    const buttons = screen.getAllByRole('button', { name: /^测试发送$|^Send test$/ });
    fireEvent.click(buttons[buttons.length - 1]);
    await waitFor(() => expect(testChannel).toHaveBeenCalledTimes(1));
    expect(saveChannel).not.toHaveBeenCalled();
  });

  it('groups saved instances inside their channel types without a separate table', async () => {
    const base = { enabled: true, version: 1, config: {}, configuredSecrets: [], createTime: '2026-11-19T00:00:00Z', updateTime: '2026-11-19T00:00:00Z', createUserId: '1', updateUserId: '1' };
    fetchChannels.mockResolvedValue([
      { ...base, id: '1', name: 'Telegram production', type: 'telegram' },
      { ...base, id: '2', name: 'Telegram testing', type: 'telegram' },
      { ...base, id: '3', name: 'Feishu operations', type: 'feishu' },
    ]);
    render(<NotificationChannelsPanel />);
    await screen.findByText('Telegram production');
    const telegram = within(screen.getByTestId('notification-type-telegram'));
    expect(telegram.getByText('Telegram production')).toBeTruthy();
    expect(telegram.getByText('Telegram testing')).toBeTruthy();
    expect(telegram.queryByText('Feishu operations')).toBeNull();
    expect(telegram.getAllByRole('listitem')).toHaveLength(2);
    expect(telegram.queryByText(/创建时间|Created/)).toBeNull();
    expect(telegram.queryByText(/更新时间|Updated/)).toBeNull();
    expect(within(screen.getByTestId('notification-type-feishu')).getByText('Feishu operations')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(telegram.getByRole('button', { name: /^新建实例$|^New instance$/ })).toBeTruthy();
    fireEvent.click(within(screen.getByTestId('notification-instance-1')).getByRole('button', { name: /^编辑$|^Edit$/ }));
    expect(await screen.findByDisplayValue('Telegram production')).toBeTruthy();
  });

  it('never renders saved secrets and preserves them when toggling', async () => {
    fetchChannels.mockResolvedValue([{
      id: '9007199254740993', name: 'Ops', type: 'telegram', enabled: true, version: 2,
      config: { chatId: '456' }, configuredSecrets: ['token'],
      createTime: '2026-11-19T00:00:00Z', updateTime: '2026-11-19T00:00:00Z',
      createUserId: '1', updateUserId: '1',
    }]);
    render(<NotificationChannelsPanel />);
    fireEvent.click(await screen.findByRole('button', { name: /^停用$|^Disable$/ }));
    await waitFor(() => expect(saveChannel).toHaveBeenCalledWith(expect.objectContaining({
      id: '9007199254740993', enabled: false, config: { chatId: '456' }, keepSecrets: ['token'],
    })));
  });
});
