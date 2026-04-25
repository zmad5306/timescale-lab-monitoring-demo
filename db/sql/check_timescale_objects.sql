SELECT hypertable_name, num_dimensions, chunk_sizing_func_schema, chunk_sizing_func_name
FROM timescaledb_information.hypertables
WHERE hypertable_name = 'sensor_readings';

SELECT view_name, materialized_only, compression_enabled
FROM timescaledb_information.continuous_aggregates
WHERE view_name LIKE 'sensor_readings_%'
ORDER BY view_name;

SELECT hypertable_name, job_id, schedule_interval, config
FROM timescaledb_information.jobs
WHERE hypertable_name LIKE 'sensor_readings%'
ORDER BY hypertable_name, job_id;
