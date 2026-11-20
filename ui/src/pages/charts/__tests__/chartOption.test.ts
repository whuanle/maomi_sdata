import { describe, expect, it } from 'vitest';
import { buildChartOption } from '../chartOption';

const range = { from: '2026-11-18T00:00:00Z', to: '2026-11-18T01:00:00Z' };
describe('Explorer shared renderer', () => {
  it('preserves all group dimensions in categorical charts', () => {
    const option = buildChartOption({ columns: ['service', 'severity', 'value'], rows: [['api', 'ERROR', 3], ['api', 'INFO', 7]] }, 'bar', range, [], true, 'line', 'gray');
    expect(option?.xAxis).toMatchObject({ data: ['api / ERROR', 'api / INFO'] });
    expect(option?.series).toMatchObject([{ data: [3, 7] }]);
  });
  it('pivots long-form series independently with null gaps', () => {
    const option = buildChartOption({ columns: ['ts_bucket', 'series', 'service', 'value'], rows: [[range.from, 'A', 'api', 3], [range.to, 'B', 'db', 7]] }, 'time_series', range, [], false, 'bar', 'gray');
    expect(option?.xAxis).toMatchObject({ type: 'time', min: Date.parse(range.from), max: Date.parse(range.to) });
    expect(option?.series).toMatchObject([
      { name: 'A / api', type: 'bar', connectNulls: false, data: [[Date.parse(range.from), 3], [Date.parse(range.to), null]] },
      { name: 'B / db', type: 'bar', data: [[Date.parse(range.from), null], [Date.parse(range.to), 7]] },
    ]);
  });
  it('uses all dimensions for pie labels and finite values only', () => {
    const option = buildChartOption({ columns: ['service', 'severity', 'value'], rows: [['api', 'ERROR', 3], ['api', 'INFO', null]] }, 'pie', range, [], true, 'line', 'gray');
    expect(option?.series).toMatchObject([{ type: 'pie', data: [{ name: 'api / ERROR', value: 3 }] }]);
  });
});
