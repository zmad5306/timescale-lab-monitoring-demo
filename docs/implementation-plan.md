# TimescaleDB Industrial Lab Monitoring Demo Implementation Plan

## Architecture

Use a monorepo with separate app, API, database, and tooling folders.

Proposed layout:

```text
/
  apps/
    web/                    # React + Highcharts Stock
  src/
    LabMonitor.Api/          # .NET 10 HTTP API
    LabMonitor.Application/  # services and contracts
    LabMonitor.Domain/       # domain models
    LabMonitor.Infrastructure/
    LabMonitor.Seed/         # synthetic data loader
  db/
    migrations/
    sql/
  docs/
  docker-compose.yml
```

## Phase 1: Repository Scaffold

Create the solution structure:

- .NET solution
- API project
- application/domain/infrastructure projects
- seed console project
- React app
- Docker Compose file
- database migration folder

Expected technology choices:

- .NET 10
- ASP.NET Core
- Npgsql
- Dapper or raw Npgsql for high-volume time-series queries
- optional EF Core for metadata tables
- React with TypeScript
- Highcharts Stock
- Vite
- TimescaleDB Docker image

Recommendation: use EF Core or simple SQL migrations for metadata, but use SQL/Npgsql directly for time-series reads and bulk seeding. The core demo is about TimescaleDB behavior, so the SQL should stay visible.

## Phase 2: Database Schema

Create metadata tables:

- `sensors`
- `sensor_channels`

Create raw telemetry table:

- `sensor_readings`

Suggested raw table:

```sql
CREATE TABLE sensor_readings (
    time timestamptz NOT NULL,
    sensor_id uuid NOT NULL REFERENCES sensors(id),
    channel_id uuid NOT NULL REFERENCES sensor_channels(id),
    value double precision NOT NULL,
    quality smallint NOT NULL DEFAULT 0,
    PRIMARY KEY (channel_id, time)
);

SELECT create_hypertable(
    'sensor_readings',
    by_range('time', INTERVAL '1 day'),
    if_not_exists => TRUE
);
```

Indexes:

```sql
CREATE INDEX ix_sensor_readings_sensor_time
    ON sensor_readings (sensor_id, time DESC);

CREATE INDEX ix_sensor_readings_channel_time
    ON sensor_readings (channel_id, time DESC);
```

Consider dropping redundant indexes after testing because the primary key already supports `channel_id, time` access.

## Phase 3: Continuous Aggregates

Create aggregate layers:

- `sensor_readings_5m`
- `sensor_readings_1h`
- `sensor_readings_1d`
- `sensor_readings_1mo`

Each aggregate should group by:

- bucket
- sensor id
- channel id

Example:

```sql
CREATE MATERIALIZED VIEW sensor_readings_5m
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('5 minutes', time) AS bucket,
    sensor_id,
    channel_id,
    avg(value) AS avg_value,
    min(value) AS min_value,
    max(value) AS max_value,
    first(value, time) AS first_value,
    last(value, time) AS last_value,
    count(*) AS sample_count
FROM sensor_readings
GROUP BY bucket, sensor_id, channel_id
WITH NO DATA;
```

Higher layers may aggregate from raw data or from the prior aggregate layer. For clarity, the first implementation can define each layer from raw readings. For a more advanced TimescaleDB demonstration, use layered aggregates:

- 5-minute from raw
- 1-hour from 5-minute
- 1-day from 1-hour
- 1-month from 1-day

Layered aggregates better demonstrate the intended architecture but require careful weighted averages. Use `sum_value` and `sample_count` if aggregating aggregates so averages remain correct.

Recommended aggregate columns for layered rollups:

- `sum_value`
- `sample_count`
- `avg_value`
- `min_value`
- `max_value`
- `first_value`
- `last_value`

## Phase 4: Timescale Policies

Add continuous aggregate refresh policies.

Initial examples:

```sql
SELECT add_continuous_aggregate_policy(
    'sensor_readings_5m',
    start_offset => INTERVAL '30 days',
    end_offset => INTERVAL '5 minutes',
    schedule_interval => INTERVAL '5 minutes'
);
```

For historical seed data, provide a script to manually refresh all aggregates:

```sql
CALL refresh_continuous_aggregate('sensor_readings_5m', NULL, NULL);
CALL refresh_continuous_aggregate('sensor_readings_1h', NULL, NULL);
CALL refresh_continuous_aggregate('sensor_readings_1d', NULL, NULL);
CALL refresh_continuous_aggregate('sensor_readings_1mo', NULL, NULL);
```

Add compression:

```sql
ALTER TABLE sensor_readings SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'sensor_id, channel_id',
    timescaledb.compress_orderby = 'time DESC'
);

SELECT add_compression_policy('sensor_readings', INTERVAL '14 days');
```

Add retention policy examples:

```sql
SELECT add_retention_policy('sensor_readings', INTERVAL '7 years');
```

For the demo, retention should not delete seeded history unexpectedly during normal use. Document how to disable or adjust it if loading exactly 7 years of data.

## Phase 5: Synthetic Data Loader

Implement `LabMonitor.Seed`.

Responsibilities:

- create deterministic sensor catalog
- create 3 to 6 channels per sensor
- generate readings for 7 years
- support shorter durations for fast testing
- bulk insert readings efficiently
- refresh aggregates after load
- optionally compress historical chunks after load

Suggested command:

```bash
dotnet run --project src/LabMonitor.Seed -- \
  --years 7 \
  --sensor-count 12 \
  --seed 4242 \
  --batch-days 7
```

Data behavior examples should be driven by channel metadata and generator hints rather than hard-coded application channel types. The domain model should treat channels as label/unit/value streams. The seed tool can still assign generation profiles to make the data realistic.

Example generated channel labels and behaviors:

- `Ambient Temperature`, `degC`: daily HVAC cycle, seasonal offset, small noise
- `Relative Humidity`, `%RH`: daily cycle, weather-like drift, occasional excursions
- `Freezer Probe A`, `degC`: stable low baseline, defrost cycles, rare door-open spikes
- `Incubator Chamber Temperature`, `degC`: tight setpoint with small oscillation
- `Vibration RMS`, `g`: low baseline with machine-operation windows and spikes
- `Shock Event Magnitude`, `g`: sparse events
- `Pressure Differential`, `Pa`: slow drift with occasional door or filter events
- `CO2 Concentration`, `ppm`: working-hours rise and overnight decay

Use channel metadata to drive:

- display label
- sample interval
- unit
- range
- generator profile
- anomaly probability

## Phase 6: Backend API

Implement services:

- `SensorCatalogService`
- `TimeSeriesQueryService`
- `ResolutionSelector`

Repositories/query objects:

- `SensorRepository`
- `ChannelRepository`
- `TimeSeriesRepository`

Endpoints:

```text
GET /api/sensors
GET /api/sensors/{sensorId}
GET /api/sensors/{sensorId}/channels
GET /api/sensors/{sensorId}/series?channelIds=...&from=...&to=...&width=...
```

Resolution selection:

```text
targetBucket = visibleRange / targetPointCount

raw if targetBucket < 5 minutes and visible range <= 6 hours
5m if targetBucket < 1 hour
1h if targetBucket < 1 day
1d if targetBucket < 1 month
1mo otherwise
```

Return shape:

```json
{
  "sensorId": "...",
  "from": "2020-01-01T00:00:00Z",
  "to": "2026-12-31T23:59:59Z",
  "resolution": "1d",
  "pointCount": 2557,
  "series": [
    {
      "channelId": "...",
      "name": "Room Temperature",
      "unit": "degC",
      "points": [[1577836800000, 21.4]]
    }
  ]
}
```

Use epoch milliseconds for Highcharts compatibility.

## Phase 7: Frontend

Build a monitoring-oriented React UI.

Main screen:

- left sensor list
- channel selector for selected sensor
- Highcharts Stock chart for selected sensor
- compact metadata panel
- selected resolution indicator
- range selector and navigator enabled

Chart behavior:

- initial range defaults to last 30 days
- frontend sends visible `from`, `to`, and chart width
- API returns chosen resolution
- chart reloads on `afterSetExtremes`
- debounce range changes
- keep the existing series visible while the next resolution is loading
- cancel or ignore stale requests when the user continues zooming or panning
- update series in place rather than remounting the chart
- avoid axis resets unless the selected channels or units actually changed
- preserve selected channels during sensor changes when possible

Highcharts details:

- use Highcharts Stock
- use one series per selected channel
- use multiple y-axes when units differ
- use tooltip shared across channels
- use navigator for long history
- avoid rendering excessive raw points

Smooth resolution switching:

- treat zoom and pan events as intent changes, not immediate chart resets
- debounce `afterSetExtremes` by roughly 150 to 300 ms
- use an abortable request per chart query
- track a monotonically increasing request id so late responses cannot overwrite newer ranges
- show a subtle loading state over the existing chart instead of clearing data
- only replace series data after all selected channels for the requested range have returned
- preserve current extremes when applying returned points
- consider prefetching an adjacent aggregate level when crossing common thresholds if switching still feels abrupt
- prefer stable y-axis configuration per unit so downsampling changes do not create visual jumps

## Phase 8: Docker Compose

Compose services:

- `db`: TimescaleDB
- `api`: .NET API
- `web`: React dev server or built static app

Optional profiles:

- `seed`: run synthetic loader
- `admin`: database admin UI

Environment:

- `POSTGRES_DB=labmonitor`
- `POSTGRES_USER=labmonitor`
- `POSTGRES_PASSWORD=labmonitor`
- API connection string via environment variable

## Phase 9: Verification

Database checks:

- hypertable exists
- chunks are created
- continuous aggregates contain rows
- compression policy exists
- retention policy exists
- query plans use aggregate views for wide ranges

API checks:

- sensor catalog returns seeded sensors
- narrow range returns `raw`
- medium range returns `5m` or `1h`
- full range returns `1mo`
- selected channels are filtered correctly

Frontend checks:

- chart renders selected sensor channels
- zooming changes API resolution
- zooming across resolution boundaries keeps the previous data visible until replacement data is ready
- rapid zoom and pan interactions do not allow stale API responses to overwrite the current chart
- full 7-year range remains responsive
- multiple units render without unreadable axes

## Phase 10: Documentation

Add:

- README with quick start
- database design notes
- API examples
- chart downsampling explanation
- compressed chunk mutation strategy
- troubleshooting for aggregate refresh and compression

## Implementation Order

1. Create monorepo scaffold and Docker Compose.
2. Add database schema and TimescaleDB migration scripts.
3. Add seed catalog and data loader.
4. Add continuous aggregates and refresh scripts.
5. Add compression and retention policies.
6. Build API catalog endpoints.
7. Build API time-series query endpoint with resolution selection.
8. Build React shell and sensor/channel selection.
9. Integrate Highcharts Stock and zoom-triggered reloads.
10. Tune queries, indexes, chunk interval, and generated data volume.
11. Add final docs and demo walkthrough.
