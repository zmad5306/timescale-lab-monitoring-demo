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

        var source = QuerySource.For(selection.Resolution);
        var sql = $"""
            SELECT
                r.channel_id,
                c.name,
                c.unit,
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

        var series = new Dictionary<Guid, MutableSeries>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var channelId = reader.GetGuid(0);
            if (!series.TryGetValue(channelId, out var channelSeries))
            {
                channelSeries = new MutableSeries(reader.GetString(1), reader.GetString(2));
                series.Add(channelId, channelSeries);
            }

            var timeMs = reader.GetInt64(3);
            var value = reader.GetDouble(4);
            channelSeries.Points.Add([(double)timeMs, value]);
        }

        return new SensorSeriesResponse(
            query.SensorId,
            query.From,
            query.To,
            selection.Resolution,
            selection.BucketSize is null ? null : (int)selection.BucketSize.Value.TotalSeconds,
            selection.SourceName,
            selection.TargetPointCount,
            series
                .Select(item => new ChannelSeries(item.Key, item.Value.Name, item.Value.Unit, item.Value.Points))
                .OrderBy(item => item.Name)
                .ToArray());
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

    private sealed record MutableSeries(string Name, string Unit)
    {
        public List<double[]> Points { get; } = [];
    }

    private sealed record QuerySource(string TableName, string TimeColumn, string ValueColumn)
    {
        public static QuerySource For(TelemetryResolution resolution) =>
            resolution switch
            {
                TelemetryResolution.Raw => new("sensor_readings", "time", "value"),
                TelemetryResolution.FiveMinutes => new("sensor_readings_5m", "bucket", "avg_value"),
                TelemetryResolution.OneHour => new("sensor_readings_1h", "bucket", "avg_value"),
                TelemetryResolution.OneDay => new("sensor_readings_1d", "bucket", "avg_value"),
                TelemetryResolution.OneMonth => new("sensor_readings_1mo", "bucket", "avg_value"),
                _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null)
            };
    }
}
