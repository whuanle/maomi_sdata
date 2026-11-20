import { searchSql } from './search';
import { buildMapExamples, buildMapQuery, type MapQuery } from '@/pages/service-map/serviceMapQuery';
import { decodeMap } from '@/pages/service-map/serviceMapModel';

export async function loadServiceMap(query: MapQuery) {
  return decodeMap(await searchSql(buildMapQuery(query)));
}

export async function loadServiceMapExamples(query: MapQuery, selection: { source: string; target?: string }) {
  const response = await searchSql(buildMapExamples(query, selection));
  return {
    rows: response.rows.slice(0, 20).map((values) => Object.fromEntries(response.columns.map((key, index) => [key, values[index]]))),
    truncated: response.truncated || response.rows.length > 20,
  };
}

export async function loadMapServices(from: string, to: string) {
  const response = await searchSql({ from, to, rowLimit: 201, sql: "SELECT service, count(*) AS spans FROM {{traces}} WHERE ts >= $start AND ts < $end GROUP BY service ORDER BY spans DESC, service LIMIT 201" });
  return { services: response.rows.slice(0, 200).map((row) => String(row[0] ?? '')), truncated: response.truncated || response.rows.length > 200 };
}
