-- SData DuckDB 初始结构（幂等，可重复执行）
-- 按天分表（logs_/traces_/metrics_/iot_ 前缀）由写入器动态创建，见 TimeTables.CreateSql

CREATE TABLE IF NOT EXISTS sdata_info (
    boot_at TIMESTAMP NOT NULL,
    version VARCHAR NOT NULL
);

INSERT INTO sdata_info (boot_at, version)
SELECT now(), '1.0.0'
WHERE NOT EXISTS (SELECT 1 FROM sdata_info WHERE boot_at::DATE = now()::DATE);
