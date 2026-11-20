import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { MqttAuthModal } from '../MqttAuthModal';
import { fetchMqttAuthSettings, saveMqttAuthSettings } from '@/api/settings';

vi.mock('@/api/settings', () => ({ fetchMqttAuthSettings: vi.fn(), saveMqttAuthSettings: vi.fn() }));

beforeEach(() => {
  vi.resetAllMocks();
  HTMLElement.prototype.scrollIntoView = vi.fn();
  vi.mocked(fetchMqttAuthSettings).mockResolvedValue({ mode: 'none', hasKey: false });
  vi.mocked(saveMqttAuthSettings).mockResolvedValue({ mode: 'none', hasKey: false });
});

async function mode(label: string) {
  fireEvent.keyDown(screen.getByRole('combobox', { name: '认证模式' }), { key: 'ArrowDown' });
  fireEvent.click(await screen.findByRole('option', { name: label }));
}

async function ready() {
  await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeEnabled());
}

describe('MqttAuthModal', () => {
  it('requires a key when switching modes and submits a literal prefix', async () => {
    const close = vi.fn();
    render(<MqttAuthModal onClose={close} />);
    await ready();
    await mode('key 前缀匹配');
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    expect(await screen.findByText('请填写非空 key 或前缀')).toBeInTheDocument();
    expect(saveMqttAuthSettings).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('key 前缀'), { target: { value: 'sdata-demo-' } });
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(saveMqttAuthSettings).toHaveBeenCalledWith({ mode: 'prefix', key: 'sdata-demo-' }));
    expect(close).toHaveBeenCalledOnce();
  });

  it('keeps the existing key without returning or showing its value', async () => {
    vi.mocked(fetchMqttAuthSettings).mockResolvedValue({ mode: 'shared', hasKey: true });
    render(<MqttAuthModal onClose={vi.fn()} />);
    await ready();
    expect(screen.getByLabelText('统一 key')).toHaveValue('');
    expect(screen.getByText(/留空保留当前同模式值/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(saveMqttAuthSettings).toHaveBeenCalledWith({ mode: 'shared', key: '' }));
  });

  it('clears a draft key when choosing no key and does not validate a hidden field', async () => {
    vi.mocked(fetchMqttAuthSettings).mockResolvedValue({ mode: 'shared', hasKey: true });
    render(<MqttAuthModal onClose={vi.fn()} />);
    await ready();
    fireEvent.change(screen.getByLabelText('统一 key'), { target: { value: 'draft' } });
    await mode('免 key');
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() => expect(saveMqttAuthSettings).toHaveBeenCalledWith({ mode: 'none', key: '' }));
  });

  it('shows save errors and preserves the open editor', async () => {
    vi.mocked(saveMqttAuthSettings).mockRejectedValue(new Error('保存失败'));
    const close = vi.fn();
    render(<MqttAuthModal onClose={close} />);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: '保存' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('保存失败');
    expect(close).not.toHaveBeenCalled();
  });

  it('blocks saving when settings could not be loaded', async () => {
    vi.mocked(fetchMqttAuthSettings).mockRejectedValue(new Error('读取失败'));
    render(<MqttAuthModal onClose={vi.fn()} />);
    expect(await screen.findByRole('alert')).toHaveTextContent('读取失败');
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled();
  });
});
