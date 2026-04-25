# Timescale Lab Monitoring Demo

Demo application for modeling industrial laboratory sensor telemetry with TimescaleDB, .NET 10, React, and Highcharts Stock.

Current implementation includes:

- .NET 10 solution structure
- sensor/channel domain model
- API endpoints backed by PostgreSQL/TimescaleDB
- resolution selection logic for chart downsampling
- TimescaleDB schema, continuous aggregate, compression, and retention scripts
- Docker Compose TimescaleDB service
- React + Highcharts Stock frontend

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

To seed metadata plus a small telemetry window:

```bash
dotnet run --project src/LabMonitor.Seed -- \
  --readings \
  --days 30 \
  --refresh-aggregates
```

To generate the full 7-year demo history:

```bash
dotnet run --project src/LabMonitor.Seed -- \
  --readings \
  --years 7 \
  --batch-days 7 \
  --refresh-aggregates
```

The readings loader bulk loads with PostgreSQL binary `COPY`. It deletes each requested batch window before loading it, so rerunning the same date range is repeatable.

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
- `GET /api/sensors/{sensorId}/series?from=2024-01-01T00:00:00Z&to=2024-02-01T00:00:00Z&width=1200`
- `GET /api/resolution?from=2024-01-01T00:00:00Z&to=2024-02-01T00:00:00Z&width=1200`

The series endpoint automatically selects raw data or a continuous aggregate based on the requested range and chart width. If `channelIds` is omitted, it returns up to six enabled channels for the sensor. To request specific channels, pass comma-separated IDs:

```text
GET /api/sensors/{sensorId}/series?channelIds={channelId1},{channelId2}&from=2024-01-01T00:00:00Z&to=2024-01-02T00:00:00Z&width=1200
```

## Run The Frontend

```bash
cd apps/web
npm install
npm run dev
```

The Vite dev server proxies `/api` to the .NET API at `http://localhost:5053`.

The chart keeps the current data visible while range changes debounce and the next resolution loads. Stale requests are aborted or ignored so quick zoom/pan interactions do not overwrite the newest range.

## Verify The Build

```bash
dotnet build LabMonitor.slnx
cd apps/web && npm run build
```

## Current API Data Source

The API reads the sensor catalog from PostgreSQL through Npgsql. Make sure the database is running and metadata has been seeded before calling the catalog endpoints.

## Next Slice

The next implementation chunk should refine the frontend interaction model and add smoother chart loading states around range changes.
