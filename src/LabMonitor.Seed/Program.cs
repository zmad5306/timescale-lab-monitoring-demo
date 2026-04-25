using LabMonitor.Domain;
using Npgsql;
using NpgsqlTypes;

var options = SeedOptions.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine("""
        LabMonitor.Seed

        Seeds deterministic sensor/channel metadata and optional synthetic readings.

        Options:
          --connection-string <value>  PostgreSQL connection string.
          --readings                   Generate and bulk-load synthetic readings.
          --from <yyyy-MM-dd>          Reading start date. Defaults to today minus --days.
          --to <yyyy-MM-dd>            Reading end date. Defaults to today.
          --days <number>              Reading duration when --from/--to are omitted. Defaults to 30.
          --years <number>             Reading duration in years. Overrides --days.
          --batch-days <number>        Load readings in batches. Defaults to 7.
          --seed <number>              Deterministic generation seed. Defaults to 4242.
          --refresh-aggregates         Refresh all continuous aggregates after loading readings.
          --help                       Show help.

        Defaults to LABMONITOR_CONNECTION_STRING, then local Docker Compose credentials.
        """);
    return;
}

await using var dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();

var catalog = DemoCatalog.Create();
await SeedCatalogAsync(dataSource, catalog.Sensors, catalog.Channels);

Console.WriteLine($"Seeded {catalog.Sensors.Count} sensors and {catalog.Channels.Count} channels.");

if (options.GenerateReadings)
{
    var totalRows = await SeedReadingsAsync(dataSource, catalog.Channels, options);
    Console.WriteLine($"Seeded {totalRows:N0} readings from {options.From:u} to {options.To:u}.");

    if (options.RefreshAggregates)
    {
        await RefreshAggregatesAsync(dataSource);
        Console.WriteLine("Refreshed continuous aggregates.");
    }
}

static async Task SeedCatalogAsync(
    NpgsqlDataSource dataSource,
    IReadOnlyList<Sensor> sensors,
    IReadOnlyList<SensorChannel> channels)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    foreach (var sensor in sensors)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO sensors (id, name, location, model, installed_on, notes)
            VALUES (@id, @name, @location, @model, @installed_on, @notes)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                location = EXCLUDED.location,
                model = EXCLUDED.model,
                installed_on = EXCLUDED.installed_on,
                notes = EXCLUDED.notes;
            """, connection, transaction);

        command.Parameters.AddWithValue("id", sensor.Id);
        command.Parameters.AddWithValue("name", sensor.Name);
        command.Parameters.AddWithValue("location", sensor.Location);
        command.Parameters.AddWithValue("model", sensor.Model);
        command.Parameters.AddWithValue("installed_on", sensor.InstalledOn);
        command.Parameters.AddWithValue("notes", (object?)sensor.Notes ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    foreach (var channel in channels)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO sensor_channels (
                id,
                sensor_id,
                name,
                unit,
                nominal_sample_interval,
                expected_min,
                expected_max,
                is_enabled,
                metadata)
            VALUES (
                @id,
                @sensor_id,
                @name,
                @unit,
                @nominal_sample_interval,
                @expected_min,
                @expected_max,
                @is_enabled,
                jsonb_build_object('generatorProfile', CAST(@generator_profile AS text)))
            ON CONFLICT (id) DO UPDATE SET
                sensor_id = EXCLUDED.sensor_id,
                name = EXCLUDED.name,
                unit = EXCLUDED.unit,
                nominal_sample_interval = EXCLUDED.nominal_sample_interval,
                expected_min = EXCLUDED.expected_min,
                expected_max = EXCLUDED.expected_max,
                is_enabled = EXCLUDED.is_enabled,
                metadata = EXCLUDED.metadata;
            """, connection, transaction);

        command.Parameters.AddWithValue("id", channel.Id);
        command.Parameters.AddWithValue("sensor_id", channel.SensorId);
        command.Parameters.AddWithValue("name", channel.Name);
        command.Parameters.AddWithValue("unit", channel.Unit);
        command.Parameters.AddWithValue("nominal_sample_interval", channel.NominalSampleInterval);
        command.Parameters.AddWithValue("expected_min", (object?)channel.ExpectedMin ?? DBNull.Value);
        command.Parameters.AddWithValue("expected_max", (object?)channel.ExpectedMax ?? DBNull.Value);
        command.Parameters.AddWithValue("is_enabled", channel.IsEnabled);
        command.Parameters.AddWithValue("generator_profile", (object?)channel.GeneratorProfile ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
}

static async Task<long> SeedReadingsAsync(
    NpgsqlDataSource dataSource,
    IReadOnlyList<SensorChannel> channels,
    SeedOptions options)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    var totalRows = 0L;

    for (var batchStart = options.From; batchStart < options.To; batchStart = batchStart.AddDays(options.BatchDays))
    {
        var batchEnd = Min(batchStart.AddDays(options.BatchDays), options.To);

        await DeleteReadingWindowAsync(connection, batchStart, batchEnd);
        totalRows += await CopyReadingWindowAsync(connection, channels, batchStart, batchEnd, options.Seed);

        Console.WriteLine($"Loaded readings through {batchEnd:u}.");
    }

    return totalRows;
}

static async Task DeleteReadingWindowAsync(NpgsqlConnection connection, DateTimeOffset from, DateTimeOffset to)
{
    await using var command = new NpgsqlCommand("""
        DELETE FROM sensor_readings
        WHERE time >= @from AND time < @to;
        """, connection);

    command.Parameters.AddWithValue("from", from.UtcDateTime);
    command.Parameters.AddWithValue("to", to.UtcDateTime);

    await command.ExecuteNonQueryAsync();
}

static Task<long> CopyReadingWindowAsync(
    NpgsqlConnection connection,
    IReadOnlyList<SensorChannel> channels,
    DateTimeOffset from,
    DateTimeOffset to,
    int seed)
{
    using var importer = connection.BeginBinaryImport("""
        COPY sensor_readings (time, sensor_id, channel_id, value, quality)
        FROM STDIN (FORMAT BINARY)
        """);

    var rows = 0L;

    foreach (var channel in channels)
    {
        foreach (var reading in TelemetryGenerator.Generate(channel, from, to, seed))
        {
            importer.StartRow();
            importer.Write(reading.Time.UtcDateTime, NpgsqlDbType.TimestampTz);
            importer.Write(reading.SensorId, NpgsqlDbType.Uuid);
            importer.Write(reading.ChannelId, NpgsqlDbType.Uuid);
            importer.Write(reading.Value, NpgsqlDbType.Double);
            importer.Write(reading.Quality, NpgsqlDbType.Smallint);
            rows++;
        }
    }

    importer.Complete();
    return Task.FromResult(rows);
}

static async Task RefreshAggregatesAsync(NpgsqlDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync();

    foreach (var aggregate in new[] { "sensor_readings_5m", "sensor_readings_1h", "sensor_readings_1d", "sensor_readings_1mo" })
    {
        await using var command = new NpgsqlCommand($"CALL refresh_continuous_aggregate('{aggregate}', NULL, NULL);", connection);
        await command.ExecuteNonQueryAsync();
    }
}

static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
    left <= right ? left : right;

internal sealed record SeedOptions(
    string ConnectionString,
    bool ShowHelp,
    bool GenerateReadings,
    DateTimeOffset From,
    DateTimeOffset To,
    int BatchDays,
    int Seed,
    bool RefreshAggregates)
{
    private const string DefaultConnectionString = "Host=localhost;Port=5432;Database=labmonitor;Username=labmonitor;Password=labmonitor";

    public static SeedOptions Parse(string[] args)
    {
        string? connectionString = Environment.GetEnvironmentVariable("LABMONITOR_CONNECTION_STRING");
        var showHelp = false;
        var generateReadings = false;
        int? days = null;
        int? years = null;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        var batchDays = 7;
        var seed = 4242;
        var refreshAggregates = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--connection-string" when index + 1 < args.Length:
                    connectionString = args[++index];
                    break;
                case "--readings":
                    generateReadings = true;
                    break;
                case "--from" when index + 1 < args.Length:
                    from = ParseDate(args[++index]);
                    break;
                case "--to" when index + 1 < args.Length:
                    to = ParseDate(args[++index]);
                    break;
                case "--days" when index + 1 < args.Length:
                    days = int.Parse(args[++index]);
                    break;
                case "--years" when index + 1 < args.Length:
                    years = int.Parse(args[++index]);
                    break;
                case "--batch-days" when index + 1 < args.Length:
                    batchDays = int.Parse(args[++index]);
                    break;
                case "--seed" when index + 1 < args.Length:
                    seed = int.Parse(args[++index]);
                    break;
                case "--refresh-aggregates":
                    refreshAggregates = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
            }
        }

        var resolvedTo = to ?? UtcDate(DateOnly.FromDateTime(DateTime.UtcNow));
        var resolvedFrom = from
            ?? (years is not null
                ? resolvedTo.AddYears(-years.Value)
                : resolvedTo.AddDays(-(days ?? 30)));

        if (resolvedTo <= resolvedFrom)
        {
            throw new ArgumentException("--to must be after --from.");
        }

        if (batchDays < 1)
        {
            throw new ArgumentException("--batch-days must be at least 1.");
        }

        return new SeedOptions(
            connectionString ?? DefaultConnectionString,
            showHelp,
            generateReadings,
            resolvedFrom,
            resolvedTo,
            batchDays,
            seed,
            refreshAggregates);
    }

    private static DateTimeOffset ParseDate(string value) =>
        UtcDate(DateOnly.Parse(value));

    private static DateTimeOffset UtcDate(DateOnly value) =>
        new(DateTime.SpecifyKind(value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));
}

internal readonly record struct SensorReading(
    DateTimeOffset Time,
    Guid SensorId,
    Guid ChannelId,
    double Value,
    short Quality);

internal static class TelemetryGenerator
{
    public static IEnumerable<SensorReading> Generate(
        SensorChannel channel,
        DateTimeOffset from,
        DateTimeOffset to,
        int seed)
    {
        var random = new Random(StableSeed(channel.Id, seed));
        var profile = channel.GeneratorProfile ?? "default";
        var offset = random.NextDouble() * 2 - 1;
        var drift = (random.NextDouble() - 0.5) * 0.002;
        var outageStart = from.AddTicks((long)((to - from).Ticks * random.NextDouble()));
        var outageDuration = TimeSpan.FromTicks((long)(channel.NominalSampleInterval.Ticks * random.Next(3, 20)));

        for (var time = from; time < to; time = time.Add(channel.NominalSampleInterval))
        {
            if (time >= outageStart && time < outageStart + outageDuration)
            {
                continue;
            }

            var value = ValueFor(profile, time, random, offset, drift);

            if (channel.ExpectedMin is not null && channel.ExpectedMax is not null)
            {
                var range = channel.ExpectedMax.Value - channel.ExpectedMin.Value;
                value = Math.Clamp(value, channel.ExpectedMin.Value - range * 0.1, channel.ExpectedMax.Value + range * 0.1);
            }

            yield return new SensorReading(time, channel.SensorId, channel.Id, Math.Round(value, 4), 0);
        }
    }

    private static double ValueFor(
        string profile,
        DateTimeOffset time,
        Random random,
        double offset,
        double drift)
    {
        var day = time.DayOfYear;
        var hour = time.TimeOfDay.TotalHours;
        var minuteOfYear = day * 24 * 60 + time.Hour * 60 + time.Minute;
        var seasonal = Math.Sin(2 * Math.PI * day / 365.25);
        var daily = Math.Sin(2 * Math.PI * hour / 24);
        var workHours = hour is >= 8 and <= 18 ? 1 : 0;
        var noise = random.NextDouble() - 0.5;
        var longDrift = drift * minuteOfYear;

        return profile switch
        {
            "ambient-temperature" => 21.5 + seasonal * 1.8 + daily * 0.8 + offset + noise * 0.35 + longDrift,
            "relative-humidity" => 45 + seasonal * 8 - daily * 4 + offset * 3 + noise * 2.5 + longDrift,
            "co2-work-hours" => 430 + workHours * (220 + Math.Max(0, Math.Sin(Math.PI * (hour - 8) / 10)) * 450) + noise * 30,
            "pressure-differential" => 18 + daily * 4 + offset * 2 + noise * 1.5 + RareSpike(random, 20, 0.001),
            "freezer-probe" => -24 + Math.Sin(2 * Math.PI * hour / 8) * 1.6 + offset * 0.8 + noise * 0.25 + RareSpike(random, 8, 0.0008),
            "door-open-count" => random.NextDouble() < (workHours == 1 ? 0.08 : 0.01) ? random.Next(1, 4) : 0,
            "compressor-current" => 7 + Math.Max(0, Math.Sin(2 * Math.PI * hour * 4)) * 5 + noise * 0.8,
            "incubator-temperature" => 37 + Math.Sin(2 * Math.PI * hour * 2) * 0.18 + offset * 0.1 + noise * 0.08,
            "incubator-co2" => 50000 + Math.Sin(2 * Math.PI * hour / 6) * 1800 + noise * 450,
            "vibration-rms" => 0.035 + workHours * 0.035 + Math.Abs(noise) * 0.025 + RareSpike(random, 0.25, 0.002),
            "shock-event" => random.NextDouble() < 0.0015 ? 0.5 + random.NextDouble() * 3.5 : Math.Abs(noise) * 0.02,
            "particle-count" => 180 + workHours * 120 + Math.Abs(noise) * 80 + RareSpike(random, 1200, 0.001),
            "line-pressure" => 620 + seasonal * 20 + daily * 8 + noise * 5,
            _ => 50 + seasonal * 5 + daily * 2 + offset + noise
        };
    }

    private static double RareSpike(Random random, double magnitude, double probability) =>
        random.NextDouble() < probability ? random.NextDouble() * magnitude : 0;

    private static int StableSeed(Guid id, int seed)
    {
        var bytes = id.ToByteArray();
        var hash = seed;

        foreach (var value in bytes)
        {
            hash = unchecked(hash * 31 + value);
        }

        return hash;
    }
}

internal sealed record DemoCatalog(
    IReadOnlyList<Sensor> Sensors,
    IReadOnlyList<SensorChannel> Channels)
{
    public static DemoCatalog Create()
    {
        var sensors = new[]
        {
            Sensor("10000000-0000-0000-0000-000000000001", "Lab Suite A Environmental Monitor", "Lab Suite A", "EnviroSense X4", 2019, 2, 14, "Primary environmental monitor for bench area."),
            Sensor("10000000-0000-0000-0000-000000000002", "Lab Suite B Environmental Monitor", "Lab Suite B", "EnviroSense X4", 2019, 5, 9, "Secondary environmental monitor for sample prep area."),
            Sensor("10000000-0000-0000-0000-000000000003", "Cold Room Monitor", "Cold Storage 2", "CryoWatch C6", 2018, 7, 3, "Tracks cold room probes and door activity."),
            Sensor("10000000-0000-0000-0000-000000000004", "Freezer Rack Monitor", "Biobank Freezer Row", "CryoWatch C6", 2018, 11, 18, "Monitors redundant freezer probes."),
            Sensor("10000000-0000-0000-0000-000000000005", "Incubator Bank Monitor", "Cell Culture Lab", "CultureGuard I5", 2020, 1, 27, "Tracks incubator chamber conditions."),
            Sensor("10000000-0000-0000-0000-000000000006", "Vibration Isolation Bench", "Analytical Lab", "MotionGuard V2", 2020, 9, 22, "Monitors vibration-sensitive equipment bench."),
            Sensor("10000000-0000-0000-0000-000000000007", "Clean Zone Pressure Monitor", "Clean Zone Entry", "AirBalance P3", 2021, 4, 6, "Tracks pressure and particle trends."),
            Sensor("10000000-0000-0000-0000-000000000008", "Solvent Storage Monitor", "Chemical Storage", "SafeStore S2", 2021, 10, 12, "Tracks storage room environment."),
            Sensor("10000000-0000-0000-0000-000000000009", "Stability Chamber 1", "Stability Lab", "StabilitySense T8", 2022, 3, 17, "Long-duration chamber monitoring."),
            Sensor("10000000-0000-0000-0000-000000000010", "Stability Chamber 2", "Stability Lab", "StabilitySense T8", 2022, 3, 17, "Long-duration chamber monitoring."),
            Sensor("10000000-0000-0000-0000-000000000011", "Utility Corridor Monitor", "Utility Corridor", "EnviroSense X2", 2022, 8, 30, "Slow-moving utility space telemetry."),
            Sensor("10000000-0000-0000-0000-000000000012", "Mass Spec Room Monitor", "Mass Spectrometry Room", "MotionGuard V2", 2023, 2, 2, "Environmental and vibration monitor.")
        };

        var channels = new List<SensorChannel>();
        AddEnvironmental(channels, sensors[0].Id, "20000000-0001");
        AddEnvironmental(channels, sensors[1].Id, "20000000-0002");
        AddColdStorage(channels, sensors[2].Id, "20000000-0003");
        AddColdStorage(channels, sensors[3].Id, "20000000-0004");
        AddIncubator(channels, sensors[4].Id, "20000000-0005");
        AddVibration(channels, sensors[5].Id, "20000000-0006");
        AddCleanZone(channels, sensors[6].Id, "20000000-0007");
        AddEnvironmental(channels, sensors[7].Id, "20000000-0008");
        AddIncubator(channels, sensors[8].Id, "20000000-0009");
        AddIncubator(channels, sensors[9].Id, "20000000-0010");
        AddUtility(channels, sensors[10].Id, "20000000-0011");
        AddVibration(channels, sensors[11].Id, "20000000-0012");

        return new DemoCatalog(sensors, channels);
    }

    private static Sensor Sensor(string id, string name, string location, string model, int year, int month, int day, string notes) =>
        new(Guid.Parse(id), name, location, model, new DateOnly(year, month, day), notes);

    private static SensorChannel Channel(string prefix, int ordinal, Guid sensorId, string name, string unit, TimeSpan interval, double? min, double? max, string profile) =>
        new(Guid.Parse($"{prefix}-0000-0000-0000-{ordinal:000000000000}"), sensorId, name, unit, interval, min, max, true, profile);

    private static void AddEnvironmental(List<SensorChannel> channels, Guid sensorId, string prefix)
    {
        channels.Add(Channel(prefix, 1, sensorId, "Ambient Temperature", "degC", TimeSpan.FromMinutes(1), 15, 30, "ambient-temperature"));
        channels.Add(Channel(prefix, 2, sensorId, "Relative Humidity", "%RH", TimeSpan.FromMinutes(1), 20, 80, "relative-humidity"));
        channels.Add(Channel(prefix, 3, sensorId, "CO2 Concentration", "ppm", TimeSpan.FromMinutes(5), 350, 2000, "co2-work-hours"));
        channels.Add(Channel(prefix, 4, sensorId, "Pressure Differential", "Pa", TimeSpan.FromMinutes(1), -20, 50, "pressure-differential"));
    }

    private static void AddColdStorage(List<SensorChannel> channels, Guid sensorId, string prefix)
    {
        channels.Add(Channel(prefix, 1, sensorId, "Freezer Probe A", "degC", TimeSpan.FromMinutes(1), -35, -10, "freezer-probe"));
        channels.Add(Channel(prefix, 2, sensorId, "Freezer Probe B", "degC", TimeSpan.FromMinutes(1), -35, -10, "freezer-probe"));
        channels.Add(Channel(prefix, 3, sensorId, "Door Open Count", "count", TimeSpan.FromMinutes(5), 0, 10, "door-open-count"));
        channels.Add(Channel(prefix, 4, sensorId, "Compressor Current", "A", TimeSpan.FromMinutes(1), 0, 20, "compressor-current"));
    }

    private static void AddIncubator(List<SensorChannel> channels, Guid sensorId, string prefix)
    {
        channels.Add(Channel(prefix, 1, sensorId, "Chamber Temperature", "degC", TimeSpan.FromMinutes(1), 30, 45, "incubator-temperature"));
        channels.Add(Channel(prefix, 2, sensorId, "Relative Humidity", "%RH", TimeSpan.FromMinutes(1), 50, 95, "relative-humidity"));
        channels.Add(Channel(prefix, 3, sensorId, "CO2 Concentration", "ppm", TimeSpan.FromMinutes(1), 3000, 70000, "incubator-co2"));
        channels.Add(Channel(prefix, 4, sensorId, "Door Open Count", "count", TimeSpan.FromMinutes(5), 0, 10, "door-open-count"));
    }

    private static void AddVibration(List<SensorChannel> channels, Guid sensorId, string prefix)
    {
        channels.Add(Channel(prefix, 1, sensorId, "Vibration RMS", "g", TimeSpan.FromSeconds(5), 0, 0.5, "vibration-rms"));
        channels.Add(Channel(prefix, 2, sensorId, "Shock Event Magnitude", "g", TimeSpan.FromSeconds(5), 0, 5, "shock-event"));
        channels.Add(Channel(prefix, 3, sensorId, "Bench Temperature", "degC", TimeSpan.FromMinutes(1), 15, 30, "ambient-temperature"));
    }

    private static void AddCleanZone(List<SensorChannel> channels, Guid sensorId, string prefix)
    {
        channels.Add(Channel(prefix, 1, sensorId, "Pressure Differential", "Pa", TimeSpan.FromMinutes(1), -20, 80, "pressure-differential"));
        channels.Add(Channel(prefix, 2, sensorId, "Particle Count", "count/L", TimeSpan.FromMinutes(5), 0, 2500, "particle-count"));
        channels.Add(Channel(prefix, 3, sensorId, "Ambient Temperature", "degC", TimeSpan.FromMinutes(1), 15, 30, "ambient-temperature"));
        channels.Add(Channel(prefix, 4, sensorId, "Relative Humidity", "%RH", TimeSpan.FromMinutes(1), 20, 80, "relative-humidity"));
    }

    private static void AddUtility(List<SensorChannel> channels, Guid sensorId, string prefix)
    {
        channels.Add(Channel(prefix, 1, sensorId, "Ambient Temperature", "degC", TimeSpan.FromMinutes(5), 5, 40, "ambient-temperature"));
        channels.Add(Channel(prefix, 2, sensorId, "Relative Humidity", "%RH", TimeSpan.FromMinutes(5), 10, 90, "relative-humidity"));
        channels.Add(Channel(prefix, 3, sensorId, "Line Pressure", "kPa", TimeSpan.FromMinutes(5), 300, 900, "line-pressure"));
    }
}
