import savedFixture from '../../../../../tests/SData.Store.Tests/Fixtures/heatmap-scales.json';
import { describe, expect, it } from 'vitest';
import type { HeatmapConfig } from '@/api/chartTypes';
import type { SearchSqlParameters } from '@/api/types';
import { buildHeatmapSql } from '../heatmap';

describe('Heatmap DuckDB fixtures', () => {
  it('keeps real database fixtures identical to generated SQL', () => {
    for (const fixture of savedFixture) {
      const parameters: SearchSqlParameters = { service: 'api', chart_bucket: '1 minute' };
      const sql = buildHeatmapSql(fixture.domain, fixture.config as HeatmapConfig,
        `${fixture.domain === 'metrics' ? 'name' : 'service'} = $service`, 'CAST($chart_bucket AS INTERVAL)', parameters);
      expect({ sql, parameters }).toEqual({ sql: fixture.sql, parameters: fixture.parameters });
    }
  });
});
