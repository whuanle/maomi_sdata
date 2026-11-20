import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import dayjs from 'dayjs';
import '@/i18n';
import { TimeRangePicker, type TimeRange } from '../TimeRangePicker';

/** 固定初始值：2026-11-17 10:00 ~ 12:00（自定义绝对区间）. */
const INITIAL: TimeRange = {
  from: '2026-11-17T10:00:00+08:00',
  to: '2026-11-17T12:00:00+08:00',
};

function renderPicker(onChange = vi.fn()) {
  render(<TimeRangePicker value={INITIAL} onChange={onChange} testId="tr-test" />);
  fireEvent.click(screen.getByTestId('tr-test'));
  return onChange;
}

describe('TimeRangePicker', () => {
  it('Time range 页签：Apply 确认起止区间（原「查询」按钮已改名）', () => {
    const onChange = renderPicker();
    expect(screen.getByText('应用')).toBeTruthy();
    fireEvent.change(screen.getByLabelText(/开始时间/), { target: { value: '2026-11-17 08:00' } });
    fireEvent.change(screen.getByLabelText(/结束时间/), { target: { value: '2026-11-17 09:00' } });
    fireEvent.click(screen.getByText('应用'));
    expect(onChange).toHaveBeenCalledWith({
      from: dayjs('2026-11-17 08:00').toISOString(),
      to: dayjs('2026-11-17 09:00').toISOString(),
    });
  });

  it('Around a time 页签：时间点 ± 时长（默认 15m）', () => {
    const onChange = renderPicker();
    fireEvent.click(screen.getByText('某个时间前后'));
    fireEvent.change(screen.getByLabelText('时间'), { target: { value: '2026-11-17 14:00' } });
    fireEvent.click(screen.getByText('应用'));
    const call = onChange.mock.calls[0][0] as TimeRange;
    expect(dayjs(call.from).valueOf()).toBe(dayjs('2026-11-17 14:00').subtract(15, 'minute').valueOf());
    expect(dayjs(call.to).valueOf()).toBe(dayjs('2026-11-17 14:00').add(15, 'minute').valueOf());
  });

  it('自然语言输入：1h ago / now 可直接解析', () => {
    const onChange = renderPicker();
    fireEvent.change(screen.getByLabelText(/开始时间/), { target: { value: '1h ago' } });
    fireEvent.change(screen.getByLabelText(/结束时间/), { target: { value: 'now' } });
    fireEvent.click(screen.getByText('应用'));
    const call = onChange.mock.calls[0][0] as TimeRange;
    expect(dayjs(call.to).valueOf()).toBeGreaterThan(dayjs(call.from).valueOf());
    expect(dayjs().diff(dayjs(call.to))).toBeLessThan(5_000);
    expect(dayjs().diff(dayjs(call.from))).toBeGreaterThan(59 * 60 * 1000);
  });

  it('1h back：区间整体平移 1 小时并即时生效（面板保持打开）', () => {
    const onChange = renderPicker();
    fireEvent.click(screen.getByText('回退 1h'));
    expect(onChange).toHaveBeenCalledTimes(1);
    const call = onChange.mock.calls[0][0] as TimeRange;
    expect(dayjs(call.from).format('YYYY-MM-DD HH:mm')).toBe('2026-11-17 09:00');
    expect(dayjs(call.to).format('YYYY-MM-DD HH:mm')).toBe('2026-11-17 11:00');
  });

  it('无法识别的时间输入：标红提示且不生效', () => {
    const onChange = renderPicker();
    fireEvent.change(screen.getByLabelText(/开始时间/), { target: { value: 'abc' } });
    fireEvent.click(screen.getByText('应用'));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByText(/无法识别的时间输入/)).toBeTruthy();
  });
});
