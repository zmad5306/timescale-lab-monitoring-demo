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

## Next Slice

The next implementation chunk should connect the API to PostgreSQL/TimescaleDB and implement the synthetic seed loader.
