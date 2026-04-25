CREATE MATERIALIZED VIEW IF NOT EXISTS sensor_readings_5m
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('5 minutes', time) AS bucket,
    sensor_id,
    channel_id,
    sum(value) AS sum_value,
    count(*) AS sample_count,
    avg(value) AS avg_value,
    min(value) AS min_value,
    max(value) AS max_value,
    first(value, time) AS first_value,
    last(value, time) AS last_value
FROM sensor_readings
GROUP BY bucket, sensor_id, channel_id
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS sensor_readings_1h
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 hour', bucket) AS bucket,
    sensor_id,
    channel_id,
    sum(sum_value) AS sum_value,
    sum(sample_count) AS sample_count,
    sum(sum_value) / NULLIF(sum(sample_count), 0) AS avg_value,
    min(min_value) AS min_value,
    max(max_value) AS max_value,
    first(first_value, bucket) AS first_value,
    last(last_value, bucket) AS last_value
FROM sensor_readings_5m
GROUP BY time_bucket('1 hour', bucket), sensor_id, channel_id
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS sensor_readings_1d
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 day', bucket) AS bucket,
    sensor_id,
    channel_id,
    sum(sum_value) AS sum_value,
    sum(sample_count) AS sample_count,
    sum(sum_value) / NULLIF(sum(sample_count), 0) AS avg_value,
    min(min_value) AS min_value,
    max(max_value) AS max_value,
    first(first_value, bucket) AS first_value,
    last(last_value, bucket) AS last_value
FROM sensor_readings_1h
GROUP BY time_bucket('1 day', bucket), sensor_id, channel_id
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS sensor_readings_1mo
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 month', bucket) AS bucket,
    sensor_id,
    channel_id,
    sum(sum_value) AS sum_value,
    sum(sample_count) AS sample_count,
    sum(sum_value) / NULLIF(sum(sample_count), 0) AS avg_value,
    min(min_value) AS min_value,
    max(max_value) AS max_value,
    first(first_value, bucket) AS first_value,
    last(last_value, bucket) AS last_value
FROM sensor_readings_1d
GROUP BY time_bucket('1 month', bucket), sensor_id, channel_id
WITH NO DATA;
