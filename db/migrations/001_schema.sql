CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS sensors (
    id uuid PRIMARY KEY,
    name text NOT NULL,
    location text NOT NULL,
    model text NOT NULL,
    installed_on date NOT NULL,
    notes text NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS sensor_channels (
    id uuid PRIMARY KEY,
    sensor_id uuid NOT NULL REFERENCES sensors(id) ON DELETE CASCADE,
    name text NOT NULL,
    unit text NOT NULL,
    nominal_sample_interval interval NOT NULL,
    expected_min double precision NULL,
    expected_max double precision NULL,
    is_enabled boolean NOT NULL DEFAULT true,
    chart_options jsonb NOT NULL DEFAULT '{}'::jsonb,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT uq_sensor_channels_sensor_name UNIQUE (sensor_id, name)
);

CREATE TABLE IF NOT EXISTS sensor_readings (
    time timestamptz NOT NULL,
    sensor_id uuid NOT NULL REFERENCES sensors(id) ON DELETE CASCADE,
    channel_id uuid NOT NULL REFERENCES sensor_channels(id) ON DELETE CASCADE,
    value double precision NOT NULL,
    quality smallint NOT NULL DEFAULT 0,
    PRIMARY KEY (channel_id, time)
);

SELECT create_hypertable(
    'sensor_readings',
    by_range('time', INTERVAL '1 day'),
    if_not_exists => TRUE
);

CREATE INDEX IF NOT EXISTS ix_sensor_readings_sensor_time
    ON sensor_readings (sensor_id, time DESC);

CREATE INDEX IF NOT EXISTS ix_sensor_readings_channel_time
    ON sensor_readings (channel_id, time DESC);

CREATE INDEX IF NOT EXISTS ix_sensor_channels_sensor_enabled
    ON sensor_channels (sensor_id, is_enabled);
