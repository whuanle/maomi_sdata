-- 指标 rollup 汇总表（契约 C6，幂等可重复执行）
-- 同构两表：metrics_rollup_5m / metrics_rollup_1h，列 (name, labels_hash, labels, bucket_ts, min, max, sum, count)
-- 由 RollupService 周期回填（5m 表滞后 ≤10 分钟、1h 表滞后 ≤2 小时）；
-- 查询分派：MetricStore 跨度 > 24h 且聚合 ∈ {avg,sum,min,max,count} 走 metrics_rollup_1h，否则原始天表。

CREATE TABLE IF NOT EXISTS metrics_rollup_5m (
    name VARCHAR NOT NULL,
    labels_hash VARCHAR NOT NULL,
    labels VARCHAR,
    bucket_ts TIMESTAMP NOT NULL,
    min DOUBLE,
    max DOUBLE,
    sum DOUBLE,
    count BIGINT
);
DROP INDEX IF EXISTS metrics_rollup_5m_qidx;

CREATE TABLE IF NOT EXISTS metrics_rollup_1h (
    name VARCHAR NOT NULL,
    labels_hash VARCHAR NOT NULL,
    labels VARCHAR,
    bucket_ts TIMESTAMP NOT NULL,
    min DOUBLE,
    max DOUBLE,
    sum DOUBLE,
    count BIGINT
);
DROP INDEX IF EXISTS metrics_rollup_1h_qidx;

CREATE TABLE IF NOT EXISTS sdata_rollup_watermarks (
    table_name VARCHAR PRIMARY KEY,
    watermark_ts TIMESTAMP NOT NULL
);
