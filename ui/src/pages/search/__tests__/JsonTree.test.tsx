import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { JsonTree } from '../JsonTree';

const nested = {
  service: 'api-gateway',
  attributes: {
    http: { method: 'POST', route: '/api/orders' },
    count: 3,
    flags: [true, 'x'],
    empty: null,
  },
};

describe('JsonTree', () => {
  it('递归渲染容器节点（{n} keys/[n] items）与叶子值（字符串带引号）', () => {
    render(<JsonTree value={nested} />);
    expect(screen.getByText('service')).toBeTruthy();
    expect(screen.getByText('"api-gateway"')).toBeTruthy();
    expect(screen.getByText('http')).toBeTruthy();
    expect(screen.getByText('method')).toBeTruthy();
    expect(screen.getByText('"POST"')).toBeTruthy();
    expect(screen.getByText('{2 keys}')).toBeTruthy();
    expect(screen.getByText('{4 keys}')).toBeTruthy();
    expect(screen.getByText('[2 items]')).toBeTruthy();
    expect(screen.getByText('null')).toBeTruthy();
  });

  it('容器节点点击收展（标签或 chevron 均可触发）', () => {
    render(<JsonTree value={nested} defaultExpanded={false} />);
    // 默认收起：子键不可见
    expect(screen.queryByText('method')).toBeNull();
    // 点击容器标签逐层展开（attributes → http）
    fireEvent.click(screen.getByText('{4 keys}'));
    expect(screen.queryByText('method')).toBeNull();
    fireEvent.click(screen.getByText('{2 keys}'));
    expect(screen.getByText('method')).toBeTruthy();
    // 点击 chevron 收起
    fireEvent.click(screen.getByRole('button', { name: 'collapse: attributes' }));
    expect(screen.queryByText('method')).toBeNull();
    // 重新展开后 chevron 状态翻转
    fireEvent.click(screen.getByRole('button', { name: 'expand: attributes' }));
    expect(screen.getByText('count')).toBeTruthy();
  });
});
