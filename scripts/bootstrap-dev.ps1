$ErrorActionPreference = 'Stop'

$rootDir = Resolve-Path (Join-Path $PSScriptRoot '..')
$dbUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { 'labmonitor' }
$dbName = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { 'labmonitor' }
$connectionString = if ($env:LABMONITOR_CONNECTION_STRING) { $env:LABMONITOR_CONNECTION_STRING } else { 'Host=localhost;Port=5432;Database=labmonitor;Username=labmonitor;Password=labmonitor' }
$seedYears = if ($env:LABMONITOR_SEED_YEARS) { $env:LABMONITOR_SEED_YEARS } else { '1' }
$seedBatchDays = if ($env:LABMONITOR_SEED_BATCH_DAYS) { $env:LABMONITOR_SEED_BATCH_DAYS } else { '7' }

Set-Location $rootDir

function Invoke-DbScalar {
    param([string]$Sql)

    $result = docker compose exec -T db psql -U $dbUser -d $dbName -Atc $Sql
    return ($result -join '').Trim()
}

Write-Host 'Starting TimescaleDB...'
docker compose up -d db

Write-Host 'Waiting for TimescaleDB...'
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    docker compose exec -T db pg_isready -U $dbUser -d $dbName *> $null
    if ($LASTEXITCODE -eq 0) {
        break
    }

    Start-Sleep -Seconds 2
}

docker compose exec -T db pg_isready -U $dbUser -d $dbName *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'TimescaleDB did not become ready.'
}

Write-Host 'Applying database migrations...'
Get-ChildItem -Path 'db/migrations' -Filter '*.sql' | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)"
    Get-Content -Raw $_.FullName | docker compose exec -T db psql -v ON_ERROR_STOP=1 -U $dbUser -d $dbName *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to apply migration $($_.Name)."
    }
}

$sensorCount = Invoke-DbScalar 'SELECT count(*) FROM sensors;'
$readingCount = Invoke-DbScalar 'SELECT count(*) FROM sensor_readings;'
$readingsAreStale = Invoke-DbScalar "SELECT count(*) = 0 OR max(time) < date_trunc('day', now()) - INTERVAL '1 day' FROM sensor_readings;"
$aggregateCount = Invoke-DbScalar 'SELECT count(*) FROM sensor_readings_5m;'

if ($sensorCount -eq '0' -or $readingCount -eq '0' -or $readingsAreStale -eq 't') {
    Write-Host 'Validating seed catalog...'
    dotnet run --project src/LabMonitor.Seed -- --validate-catalog

    Write-Host "Seeding metadata and $seedYears years of readings..."
    dotnet run --project src/LabMonitor.Seed -- `
        --connection-string $connectionString `
        --readings `
        --years $seedYears `
        --batch-days $seedBatchDays `
        --refresh-aggregates
} elseif ($aggregateCount -eq '0') {
    Write-Host 'Readings are seeded but aggregates are empty. Refreshing aggregates...'
    dotnet run --project src/LabMonitor.Seed -- `
        --connection-string $connectionString `
        --refresh-aggregates
} else {
    Write-Host "Database already seeded: $sensorCount sensors, $readingCount readings."
}

Write-Host 'Bootstrap complete.'
