using LabMonitor.Application;
using LabMonitor.Domain;
using Npgsql;

namespace LabMonitor.Infrastructure;

public sealed class PostgresTimeSeriesQueryService(
    NpgsqlDataSource dataSource,
    IResolutionSelector resolutionSelector) : ITimeSeriesQueryService
{
    public async Task<SensorSeriesResponse> QuerySensorSeriesAsync(
        SensorSeriesQuery query,
        CancellationToken cancellationToken)
    {
        var selection = resolutionSelector.Select(query.From, query.To, query.ChartWidth);
        var channelIds = query.ChannelIds.Count > 0
            ? query.ChannelIds
            : await GetDefaultChannelIdsAsync(query.SensorId, cancellationToken);

        if (channelIds.Count == 0)
        {
            return EmptyResponse(query, selection);
        }

        var defaultDownsampleMethods = await GetDefaultDownsampleMethodsAsync(channelIds, cancellationToken);
        var channelIdsByMethod = channelIds
            .GroupBy(channelId => DownsampleMethodFor(query, defaultDownsampleMethods, channelId))
            .ToArray();

        var series = new Dictionary<Guid, MutableSeries>();
        foreach (var group in channelIdsByMethod)
        {
            var source = QuerySource.For(selection.Resolution, group.Key);
            MergeSeries(series, await QuerySourceAsync(query, group.ToArray(), group.Key, source, cancellationToken));
        }

        var sourceName = selection.SourceName;

        if (series.Count == 0 && selection.Resolution != TelemetryResolution.Raw && selection.BucketSize is not null)
        {
            foreach (var group in channelIdsByMethod)
            {
                MergeSeries(series, await QueryRawBucketsAsync(query, group.ToArray(), group.Key, selection.BucketSize.Value, cancellationToken));
            }

            if (series.Count > 0)
            {
                sourceName = "sensor_readings";
            }
        }

        return ToResponse(query, selection, sourceName, series);
    }

    public async Task<SensorReadingRangeResponse> GetSensorReadingRangeAsync(
        Guid sensorId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT min(time), max(time)
            FROM sensor_readings
            WHERE sensor_id = @sensor_id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sensor_id", sensorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || await reader.IsDBNullAsync(0, cancellationToken))
        {
            return new SensorReadingRangeResponse(sensorId, null, null);
        }

        return new SensorReadingRangeResponse(
            sensorId,
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<Dictionary<Guid, MutableSeries>> QuerySourceAsync(
        SensorSeriesQuery query,
        IReadOnlyList<Guid> channelIds,
        DownsampleMethod downsampleMethod,
        QuerySource source,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                r.channel_id,
                c.name,
                c.unit,
                c.alarm_normal_min,
                c.alarm_normal_max,
                c.alarm_downsample_method,
                (extract(epoch FROM r.{source.TimeColumn}) * 1000)::bigint AS time_ms,
                r.{source.ValueColumn} AS value
            FROM {source.TableName} r
            INNER JOIN sensor_channels c ON c.id = r.channel_id
            WHERE r.sensor_id = @sensor_id
              AND r.channel_id = ANY(@channel_ids)
              AND r.{source.TimeColumn} >= @from
              AND r.{source.TimeColumn} < @to
            ORDER BY r.channel_id, r.{source.TimeColumn};
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sensor_id", query.SensorId);
        command.Parameters.AddWithValue("channel_ids", channelIds.ToArray());
        command.Parameters.AddWithValue("from", query.From.UtcDateTime);
        command.Parameters.AddWithValue("to", query.To.UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadSeriesAsync(reader, downsampleMethod, cancellationToken);
    }

    private async Task<Dictionary<Guid, MutableSeries>> QueryRawBucketsAsync(
        SensorSeriesQuery query,
        IReadOnlyList<Guid> channelIds,
        DownsampleMethod downsampleMethod,
        TimeSpan bucketSize,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                r.channel_id,
                c.name,
                c.unit,
                c.alarm_normal_min,
                c.alarm_normal_max,
                c.alarm_downsample_method,
                (extract(epoch FROM time_bucket(@bucket_size, r.time)) * 1000)::bigint AS time_ms,
                {RawBucketExpression(downsampleMethod)} AS value
            FROM sensor_readings r
            INNER JOIN sensor_channels c ON c.id = r.channel_id
            WHERE r.sensor_id = @sensor_id
              AND r.channel_id = ANY(@channel_ids)
              AND r.time >= @from
              AND r.time < @to
            GROUP BY r.channel_id, c.name, c.unit, c.alarm_normal_min, c.alarm_normal_max, c.alarm_downsample_method, time_ms
            ORDER BY r.channel_id, time_ms;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sensor_id", query.SensorId);
        command.Parameters.AddWithValue("channel_ids", channelIds.ToArray());
        command.Parameters.AddWithValue("from", query.From.UtcDateTime);
        command.Parameters.AddWithValue("to", query.To.UtcDateTime);
        command.Parameters.AddWithValue("bucket_size", bucketSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadSeriesAsync(reader, downsampleMethod, cancellationToken);
    }

    private static SensorSeriesResponse ToResponse(
        SensorSeriesQuery query,
        ResolutionSelection selection,
        string sourceName,
        Dictionary<Guid, MutableSeries> series) =>
        new(
            query.SensorId,
            query.From,
            query.To,
            selection.Resolution,
            selection.BucketSize is null ? null : (int)selection.BucketSize.Value.TotalSeconds,
            sourceName,
            selection.TargetPointCount,
            series
                .Select(item => new ChannelSeries(item.Key, item.Value.Name, item.Value.Unit, item.Value.DownsampleMethod, item.Value.Alarm, item.Value.Points))
                .OrderBy(item => item.Name)
                .ToArray());

    private static async Task<Dictionary<Guid, MutableSeries>> ReadSeriesAsync(
        NpgsqlDataReader reader,
        DownsampleMethod downsampleMethod,
        CancellationToken cancellationToken)
    {
        var series = new Dictionary<Guid, MutableSeries>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var channelId = reader.GetGuid(0);
            if (!series.TryGetValue(channelId, out var channelSeries))
            {
                channelSeries = new MutableSeries(
                    reader.GetString(1),
                    reader.GetString(2),
                    downsampleMethod,
                    new AlarmConfiguration(
                        reader.IsDBNull(3) ? null : reader.GetDouble(3),
                        reader.IsDBNull(4) ? null : reader.GetDouble(4),
                        ParseDownsampleMethod(reader.GetString(5))));
                series.Add(channelId, channelSeries);
            }

            var timeMs = reader.GetInt64(6);
            var value = reader.GetDouble(7);
            channelSeries.Points.Add([(double)timeMs, value]);
        }

        return series;
    }

    private async Task<IReadOnlyList<Guid>> GetDefaultChannelIdsAsync(Guid sensorId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM sensor_channels
            WHERE sensor_id = @sensor_id AND is_enabled
            ORDER BY name
            LIMIT 6;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sensor_id", sensorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var channelIds = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            channelIds.Add(reader.GetGuid(0));
        }

        return channelIds;
    }

    private static SensorSeriesResponse EmptyResponse(SensorSeriesQuery query, ResolutionSelection selection) =>
        new(
            query.SensorId,
            query.From,
            query.To,
            selection.Resolution,
            selection.BucketSize is null ? null : (int)selection.BucketSize.Value.TotalSeconds,
            selection.SourceName,
            selection.TargetPointCount,
            []);

    private async Task<IReadOnlyDictionary<Guid, DownsampleMethod>> GetDefaultDownsampleMethodsAsync(
        IReadOnlyList<Guid> channelIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, alarm_downsample_method
            FROM sensor_channels
            WHERE id = ANY(@channel_ids);
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("channel_ids", channelIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var methods = new Dictionary<Guid, DownsampleMethod>();
        while (await reader.ReadAsync(cancellationToken))
        {
            methods[reader.GetGuid(0)] = ParseDownsampleMethod(reader.GetString(1));
        }

        return methods;
    }

    private static DownsampleMethod DownsampleMethodFor(
        SensorSeriesQuery query,
        IReadOnlyDictionary<Guid, DownsampleMethod> defaultDownsampleMethods,
        Guid channelId) =>
        query.DownsampleMethods.TryGetValue(channelId, out var method)
            ? method
            : defaultDownsampleMethods.TryGetValue(channelId, out var defaultMethod)
                ? defaultMethod
                : DownsampleMethod.Average;

    private static void MergeSeries(Dictionary<Guid, MutableSeries> target, Dictionary<Guid, MutableSeries> source)
    {
        foreach (var item in source)
        {
            target[item.Key] = item.Value;
        }
    }

    private sealed record MutableSeries(string Name, string Unit, DownsampleMethod DownsampleMethod, AlarmConfiguration Alarm)
    {
        public List<double[]> Points { get; } = [];
    }

    private sealed record QuerySource(string TableName, string TimeColumn, string ValueColumn)
    {
        public static QuerySource For(TelemetryResolution resolution, DownsampleMethod downsampleMethod) =>
            resolution switch
            {
                TelemetryResolution.Raw => new("sensor_readings", "time", "value"),
                TelemetryResolution.FiveMinutes => new("sensor_readings_5m", "bucket", AggregateColumn(downsampleMethod)),
                TelemetryResolution.OneHour => new("sensor_readings_1h", "bucket", AggregateColumn(downsampleMethod)),
                TelemetryResolution.OneDay => new("sensor_readings_1d", "bucket", AggregateColumn(downsampleMethod)),
                TelemetryResolution.OneMonth => new("sensor_readings_1mo", "bucket", AggregateColumn(downsampleMethod)),
                _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null)
            };
    }

    private static string AggregateColumn(DownsampleMethod downsampleMethod) =>
        downsampleMethod switch
        {
            DownsampleMethod.Average => "avg_value",
            DownsampleMethod.Minimum => "min_value",
            DownsampleMethod.Maximum => "max_value",
            DownsampleMethod.First => "first_value",
            DownsampleMethod.Last => "last_value",
            _ => throw new ArgumentOutOfRangeException(nameof(downsampleMethod), downsampleMethod, null)
        };

    private static string RawBucketExpression(DownsampleMethod downsampleMethod) =>
        downsampleMethod switch
        {
            DownsampleMethod.Average => "avg(r.value)",
            DownsampleMethod.Minimum => "min(r.value)",
            DownsampleMethod.Maximum => "max(r.value)",
            DownsampleMethod.First => "first(r.value, r.time)",
            DownsampleMethod.Last => "last(r.value, r.time)",
            _ => throw new ArgumentOutOfRangeException(nameof(downsampleMethod), downsampleMethod, null)
        };

    private static DownsampleMethod ParseDownsampleMethod(string value) =>
        value switch
        {
            "average" => DownsampleMethod.Average,
            "minimum" => DownsampleMethod.Minimum,
            "maximum" => DownsampleMethod.Maximum,
            "first" => DownsampleMethod.First,
            "last" => DownsampleMethod.Last,
            _ => throw new InvalidOperationException($"Unsupported downsample method '{value}'.")
        };
}
