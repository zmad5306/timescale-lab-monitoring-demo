$ErrorActionPreference = 'Stop'

$rootDir = Resolve-Path (Join-Path $PSScriptRoot '..')
$dbUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { 'labmonitor' }
$dbName = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { 'labmonitor' }
$connectionString = if ($env:LABMONITOR_CONNECTION_STRING) { $env:LABMONITOR_CONNECTION_STRING } else { 'Host=localhost;Port=5432;Database=labmonitor;Username=labmonitor;Password=labmonitor' }
$seedYears = if ($env:LABMONITOR_SEED_YEARS) { $env:LABMONITOR_SEED_YEARS } else { '1' }
$seedBatchDays = if ($env:LABMONITOR_SEED_BATCH_DAYS) { $env:LABMONITOR_SEED_BATCH_DAYS } else { '7' }

Set-Location $rootDir

function Invoke-NativeCommand {
    param(
        [scriptblock]$Command,
        [string]$FailureMessage
    )

    $stderrFile = [System.IO.Path]::GetTempFileName()

    try {
        $output = & $Command 2> $stderrFile
        if ($LASTEXITCODE -ne 0) {
            $detail = Get-Content -Raw $stderrFile
            if ([string]::IsNullOrWhiteSpace($detail)) {
                $detail = ($output -join [Environment]::NewLine).Trim()
            }

            if ([string]::IsNullOrWhiteSpace($detail)) {
                throw $FailureMessage
            }

            throw "$FailureMessage`n$detail".Trim()
        }

        return $output
    }
    finally {
        Remove-Item -LiteralPath $stderrFile -ErrorAction SilentlyContinue
    }
}

function Invoke-DockerCompose {
    param(
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()

    try {
        $process = Start-Process -FilePath 'docker' `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $stdoutFile `
            -RedirectStandardError $stderrFile

        $stdout = if (Test-Path $stdoutFile) { Get-Content $stdoutFile } else { @() }
        $stderr = if (Test-Path $stderrFile) { Get-Content $stderrFile } else { @() }

        if ($process.ExitCode -ne 0) {
            $detail = (($stderr + $stdout) -join [Environment]::NewLine).Trim()
            if ([string]::IsNullOrWhiteSpace($detail)) {
                throw $FailureMessage
            }

            throw "$FailureMessage`n$detail".Trim()
        }

        return $stdout
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrFile -ErrorAction SilentlyContinue
    }
}

function Invoke-DbScalar {
    param([string]$Sql)

    $result = Invoke-DockerCompose -FailureMessage 'Database query failed.' -Arguments @(
        'compose', 'exec', '-T', 'db',
        'psql', '-U', $dbUser, '-d', $dbName, '-Atc', $Sql
    )
    return ($result -join '').Trim()
}

function Invoke-DotNet {
    param(
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Resolve-CountValue {
    param([string]$Value)

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return 0
    }

    $parsed = 0
    if ([int]::TryParse($trimmed, [ref]$parsed)) {
        return $parsed
    }

    return 0
}

function Resolve-StaleValue {
    param([string]$Value)

    $trimmed = $Value.Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return $true
    }

    return $trimmed -eq 't' -or $trimmed -eq 'true'
}

Write-Host 'Starting TimescaleDB...'
docker compose up -d db
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to start TimescaleDB.'
}

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
    $containerMigrationPath = "/docker-entrypoint-initdb.d/$($_.Name)"
    Invoke-DockerCompose -FailureMessage "Failed to apply migration $($_.Name)." -Arguments @(
        'compose', 'exec', '-T', 'db',
        'psql', '-v', 'ON_ERROR_STOP=1', '-U', $dbUser, '-d', $dbName, '-f', $containerMigrationPath
    ) | Out-Null
}

$sensorCount = Resolve-CountValue (Invoke-DbScalar 'SELECT count(*) FROM sensors;')
$readingCount = Resolve-CountValue (Invoke-DbScalar 'SELECT count(*) FROM sensor_readings;')
$readingsAreStale = Resolve-StaleValue (Invoke-DbScalar "SELECT count(*) = 0 OR max(time) < date_trunc('day', now()) - INTERVAL '1 day' FROM sensor_readings;")
$aggregateCount = Resolve-CountValue (Invoke-DbScalar 'SELECT count(*) FROM sensor_readings_5m;')

if ($sensorCount -eq 0 -or $readingCount -eq 0 -or $readingsAreStale) {
    Write-Host 'Validating seed catalog...'
    Invoke-DotNet -FailureMessage 'Seed catalog validation failed.' -Arguments @(
        'run', '--project', 'src/LabMonitor.Seed', '--', '--validate-catalog'
    )

    Write-Host "Seeding metadata and $seedYears years of readings..."
    Invoke-DotNet -FailureMessage 'Database seeding failed.' -Arguments @(
        'run', '--project', 'src/LabMonitor.Seed', '--',
        '--connection-string', $connectionString,
        '--readings',
        '--years', $seedYears,
        '--batch-days', $seedBatchDays,
        '--refresh-aggregates'
    )
} elseif ($aggregateCount -eq 0) {
    Write-Host 'Readings are seeded but aggregates are empty. Refreshing aggregates...'
    Invoke-DotNet -FailureMessage 'Aggregate refresh failed.' -Arguments @(
        'run', '--project', 'src/LabMonitor.Seed', '--',
        '--connection-string', $connectionString,
        '--refresh-aggregates'
    )
} else {
    Write-Host "Database already seeded: $sensorCount sensors, $readingCount readings."
}

Write-Host 'Bootstrap complete.'
