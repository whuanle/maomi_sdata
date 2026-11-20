import type { ChartSource } from './buildChartSql';

export const TIME_SERIES_EXAMPLES: Record<ChartSource, string> = {
  iot: `SELECT time_bucket(INTERVAL '1 minute', ts) AS ts_bucket,
  device_key, signal, avg(num_value) AS value
FROM {{iot}}
WHERE ts >= $start AND ts < $end
GROUP BY ts_bucket, device_key, signal
ORDER BY ts_bucket`,
  logs: `SELECT
  time_bucket(INTERVAL '1 minute', ts) AS ts_bucket,
  service AS series,
  count(*) AS value
FROM {{logs}}
WHERE ts >= $start AND ts < $end
GROUP BY ts_bucket, service
ORDER BY ts_bucket`,
  metrics: `SELECT
  time_bucket(INTERVAL '1 minute', ts) AS ts_bucket,
  name AS series,
  avg(value) AS value
FROM {{metrics}}
WHERE ts >= $start AND ts < $end
GROUP BY ts_bucket, name
ORDER BY ts_bucket`,
  sessions: `WITH sessions AS (
  SELECT project_key, session_id, min(ts) AS ts
  FROM {{sessions}}
  WHERE ts >= $start AND ts < $end
  GROUP BY project_key, session_id
)
SELECT
  time_bucket(INTERVAL '1 minute', ts) AS ts_bucket,
  project_key AS series,
  count(*) AS value
FROM sessions
GROUP BY ts_bucket, project_key
ORDER BY ts_bucket`,
  traces: `SELECT
  time_bucket(INTERVAL '1 minute', ts) AS ts_bucket,
  service AS series,
  count(*) AS value
FROM {{traces}}
WHERE ts >= $start AND ts < $end
GROUP BY ts_bucket, service
ORDER BY ts_bucket`,
};
