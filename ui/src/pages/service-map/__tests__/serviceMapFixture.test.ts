import savedFixture from '../../../../../tests/SData.Store.Tests/Fixtures/service-map.json';
import { describe, expect, it } from 'vitest';
import { buildMapQuery, buildMapExamples } from '../serviceMapQuery';

const input = { from: '2026-11-18T23:59:00Z', to: '2026-11-19T00:01:00Z', service: 'api', syntax: 'sql' as const, query: '', sampling: 100 };
describe('Service Map DuckDB fixture', () => {
  it('matches the exact frontend queries executed by the backend', () => {
    expect(savedFixture).toEqual([
      { name: 'map', request: buildMapQuery(input) },
      { name: 'sampled', request: buildMapQuery({ ...input, sampling: 50 }) },
      { name: 'examples', request: buildMapExamples(input, { source: 'api', target: 'db' }) },
    ]);
  });
});
