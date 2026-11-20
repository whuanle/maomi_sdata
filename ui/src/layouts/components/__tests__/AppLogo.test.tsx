import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AppLogo } from '../AppLogo';

describe('AppLogo', () => {
  it('使用品牌原图并提供默认名称与尺寸', () => {
    render(<AppLogo />);

    const logo = screen.getByRole('img', { name: 'SData' });
    expect(logo.getAttribute('src')).toBe(`${import.meta.env.BASE_URL}logo.png`);
    expect(logo.getAttribute('width')).toBe('30');
    expect(logo.getAttribute('height')).toBe('30');
    expect(logo.getAttribute('draggable')).toBe('false');
  });

  it('支持登录页尺寸、替代文本与布局样式', () => {
    render(<AppLogo size={48} alt="SData Logo" className="mx-auto mb-3" />);

    const logo = screen.getByRole('img', { name: 'SData Logo' });
    expect(logo.getAttribute('width')).toBe('48');
    expect(logo.getAttribute('height')).toBe('48');
    expect(logo.classList.contains('mx-auto')).toBe(true);
    expect(logo.classList.contains('mb-3')).toBe(true);
  });
});
