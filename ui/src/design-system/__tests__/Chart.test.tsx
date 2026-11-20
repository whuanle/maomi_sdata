import { render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '@/i18n';
import { Chart } from '../Chart';

const mock = vi.hoisted(() => ({
  init: vi.fn(), setOption: vi.fn(), on: vi.fn(), off: vi.fn(), resize: vi.fn(), dispose: vi.fn(),
  observe: vi.fn(), disconnect: vi.fn(), callback: undefined as undefined | (() => void),
}));
vi.mock('echarts', () => ({ init: mock.init }));

beforeEach(() => {
  vi.clearAllMocks();
  mock.init.mockImplementation(() => ({ setOption: mock.setOption, on: mock.on, off: mock.off, resize: mock.resize, dispose: mock.dispose }));
  vi.stubGlobal('ResizeObserver', class {
    constructor(callback: () => void) { mock.callback = callback; }
    observe = mock.observe;
    disconnect = mock.disconnect;
  });
});
afterEach(() => vi.unstubAllGlobals());

describe('Chart 生命周期', () => {
  it('小面板不强制 200px，resize 与销毁正确清理', () => {
    const view = render(<Chart option={{}} minHeight={0} />);
    expect(view.container.firstChild).toHaveStyle({ minHeight: '0', height: '100%' });
    expect(mock.observe).toHaveBeenCalledOnce();
    mock.callback?.();
    expect(mock.resize).toHaveBeenCalledOnce();
    view.unmount();
    expect(mock.dispose).toHaveBeenCalledOnce();
    expect(mock.disconnect).toHaveBeenCalledOnce();
  });

  it('重建 theme 后重新应用相同 option 并绑定事件', () => {
    const option = { series: [{ type: 'pie' as const, data: [1, 2] }] };
    const events = { click: vi.fn() };
    const view = render(<Chart option={option} onEvents={events} />);
    expect(mock.setOption).toHaveBeenCalledOnce();
    view.rerender(<Chart option={option} onEvents={events} theme="dark" />);
    expect(mock.init).toHaveBeenCalledTimes(2);
    expect(mock.setOption).toHaveBeenCalledTimes(2);
    expect(mock.on).toHaveBeenCalledTimes(2);
    expect(mock.off).toHaveBeenCalledWith('click');
  });
});
