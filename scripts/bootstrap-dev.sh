#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DB_USER="${POSTGRES_USER:-labmonitor}"
DB_NAME="${POSTGRES_DB:-labmonitor}"
CONNECTION_STRING="${LABMONITOR_CONNECTION_STRING:-Host=localhost;Port=5432;Database=labmonitor;Username=labmonitor;Password=labmonitor}"
SEED_YEARS="${LABMONITOR_SEED_YEARS:-7}"
SEED_BATCH_DAYS="${LABMONITOR_SEED_BATCH_DAYS:-7}"

cd "$ROOT_DIR"

echo "Starting TimescaleDB..."
docker compose up -d db

echo "Waiting for TimescaleDB..."
for _ in {1..60}; do
  if docker compose exec -T db pg_isready -U "$DB_USER" -d "$DB_NAME" >/dev/null 2>&1; then
    break
  fi

  sleep 2
done

docker compose exec -T db pg_isready -U "$DB_USER" -d "$DB_NAME" >/dev/null

echo "Applying database migrations..."
for migration in db/migrations/*.sql; do
  echo "  $(basename "$migration")"
  docker compose exec -T db psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" < "$migration" >/dev/null
done

sensor_count="$(docker compose exec -T db psql -U "$DB_USER" -d "$DB_NAME" -Atc "SELECT count(*) FROM sensors;" | tr -d '[:space:]')"
reading_count="$(docker compose exec -T db psql -U "$DB_USER" -d "$DB_NAME" -Atc "SELECT count(*) FROM sensor_readings;" | tr -d '[:space:]')"

if [[ "$sensor_count" == "0" || "$reading_count" == "0" ]]; then
  echo "Validating seed catalog..."
  dotnet run --project src/LabMonitor.Seed -- --validate-catalog

  echo "Seeding metadata and ${SEED_YEARS} years of readings..."
  dotnet run --project src/LabMonitor.Seed -- \
    --connection-string "$CONNECTION_STRING" \
    --readings \
    --years "$SEED_YEARS" \
    --batch-days "$SEED_BATCH_DAYS" \
    --refresh-aggregates
else
  echo "Database already seeded: ${sensor_count} sensors, ${reading_count} readings."
fi

echo "Bootstrap complete."
