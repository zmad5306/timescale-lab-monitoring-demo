# Timescale Lab Monitoring Demo

Demo application for modeling industrial laboratory sensor telemetry with TimescaleDB, .NET 10, React, and Highcharts Stock.

This first implementation slice includes:

- .NET 10 solution structure
- sensor/channel domain model
- initial API endpoints backed by an in-memory catalog
- resolution selection logic for chart downsampling
- TimescaleDB schema, continuous aggregate, compression, and retention scripts
- Docker Compose TimescaleDB service

## Start TimescaleDB

```bash
docker compose up -d db
```

The database initializes with the SQL files in `db/migrations`.

## Seed Sensor Metadata

```bash
dotnet run --project src/LabMonitor.Seed
```

The seed command is idempotent and loads the deterministic demo catalog:

- 12 sensors
- 3 to 4 channels per sensor
- labels and units without requiring a fixed channel type taxonomy

To target a different database:

```bash
dotnet run --project src/LabMonitor.Seed -- \
  --connection-string "Host=localhost;Port=5432;Database=labmonitor;Username=labmonitor;Password=labmonitor"
```

## Run The API

```bash
dotnet run --project src/LabMonitor.Api
```

Useful endpoints:

- `GET /api/health`
- `GET /api/sensors`
- `GET /api/sensors/{sensorId}/channels`
- `GET /api/resolution?from=2024-01-01T00:00:00Z&to=2024-02-01T00:00:00Z&width=1200`

## Verify The Build

```bash
dotnet build LabMonitor.slnx
```

## Current API Data Source

The API reads the sensor catalog from PostgreSQL through Npgsql. Make sure the database is running and metadata has been seeded before calling the catalog endpoints.

## Next Slice

The next implementation chunk should generate and bulk-load synthetic readings into `sensor_readings`, then refresh the continuous aggregates.
