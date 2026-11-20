import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { LogBodyPreview } from '../LogBodyPreview';

afterEach(() => vi.restoreAllMocks());

describe('LogBodyPreview', () => {
  it('短消息自然展示，不显示展开按钮', () => {
    render(<LogBodyPreview text="short log" onCopy={vi.fn()} />);
    expect(screen.getByTestId('log-body-preview')).toHaveTextContent('short log');
    expect(screen.queryByRole('button', { name: '展开消息体' })).not.toBeInTheDocument();
    expect(screen.getByTestId('log-body-preview')).toHaveClass('max-w-full', 'break-all', 'whitespace-pre-wrap');
  });

  it('超长消息默认收缩，展开仍限高滚动，复制保持全文', () => {
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockReturnValue(1000);
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(128);
    const text = 'long-log-line'.repeat(1000);
    const onCopy = vi.fn();
    render(<LogBodyPreview text={text} onCopy={onCopy} />);
    const preview = screen.getByTestId('log-body-preview');
    expect(preview).toHaveClass('overflow-hidden');
    const expand = screen.getByRole('button', { name: '展开消息体' });
    expect(expand).toHaveAttribute('aria-expanded', 'false');
    expect(expand).toHaveAttribute('aria-controls', preview.id);
    fireEvent.click(expand);
    expect(preview).toHaveClass('max-h-[50vh]', 'overflow-y-auto');
    fireEvent.click(screen.getByRole('button', { name: '收起消息体' }));
    expect(preview).toHaveClass('overflow-hidden');
    fireEvent.click(screen.getByRole('button', { name: '复制全部' }));
    expect(onCopy).toHaveBeenCalledWith(text);
  });
});
