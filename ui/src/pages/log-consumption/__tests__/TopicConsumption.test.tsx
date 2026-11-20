import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import '@/i18n';
import type { MqTopicRuntimeItem } from '@/api/types';
import { TopicConsumption } from '../TopicConsumption';

const topic: MqTopicRuntimeItem = {
  name: 'logs', matchMode: 'exact', state: 'consuming', matchedTopics: 1,
  receivedMessages: 1, receivedBytes: 10, persistedRecords: 1,
  parseFailures: 0, blockedMessages: 0, confirmedMessages: 1, confirmationFailures: 0,
  messagesPerSecond: 1, recordsPerSecond: 1, bytesPerSecond: 10,
  lastError: 'previous error',
};

describe('TopicConsumption', () => {
  it('hides rates and previous errors when disabled', () => {
    render(<TopicConsumption topic={topic} enabled={false} failed={false} />);
    expect(screen.getByText(/已停用|Disabled/)).toBeTruthy();
    expect(screen.queryByText(/10 B\/s/)).toBeNull();
    expect(screen.queryByText('previous error')).toBeNull();
  });

  it('shows active parse failure instead of consuming', () => {
    render(<TopicConsumption topic={{ ...topic, lastError: null, parseFailures: 2, failures: [{ stage: 'parse', code: 'json_invalid', reason: 'invalid', at: '2026-11-20T00:00:00Z', occurrences: 2 }] }} enabled failed={false} />);
    expect(screen.queryByText(/正在消费|Consuming/)).toBeNull();
    expect(screen.getByText(/消费失败|Consumer failure/)).toHaveClass('text-error');
    expect(screen.getByText(/存在解析失败|Parse failures recorded/)).toHaveClass('text-error');
  });

  it('shows confirmation failures in red without parse failures', () => {
    render(<TopicConsumption topic={{ ...topic, lastError: 'confirmation failed', confirmationFailures: 1 }} enabled failed={false} />);
    expect(screen.getByText(/最近失败|Recent failures/)).toHaveClass('text-error');
    expect(screen.queryByText(/正在消费|Consuming/)).toBeNull();
    expect(screen.getByText(/消费失败|Consumer failure/)).toHaveClass('text-error');
  });

  it('keeps healthy consumption green', () => {
    render(<TopicConsumption topic={{ ...topic, lastError: null }} enabled failed={false} />);
    expect(screen.getByText(/正在消费|Consuming/)).toHaveClass('text-success');
  });

  it('hides stale rates after refresh failure', () => {
    const { rerender } = render(<TopicConsumption topic={topic} enabled failed={false} />);
    expect(screen.getByText(/10 B\/s/)).toBeTruthy();
    rerender(<TopicConsumption topic={topic} enabled failed />);
    expect(screen.queryByText(/10 B\/s/)).toBeNull();
    expect(screen.queryByText(/0 B\/s/)).toBeNull();
  });

  it('does not invent zero rates without a snapshot', () => {
    render(<TopicConsumption enabled failed={false} />);
    expect(screen.getByText(/未知|Unknown/)).toBeTruthy();
    expect(screen.queryByText(/0 B\/s/)).toBeNull();
  });
});
