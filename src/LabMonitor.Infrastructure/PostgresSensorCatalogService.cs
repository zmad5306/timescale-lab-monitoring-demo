using LabMonitor.Application;
using LabMonitor.Domain;
using Npgsql;

namespace LabMonitor.Infrastructure;

public sealed class PostgresSensorCatalogService(NpgsqlDataSource dataSource) : ISensorCatalogService
{
    public async Task<IReadOnlyList<SensorSummary>> GetSensorsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id,
                s.name,
                s.location,
                s.model,
                s.installed_on,
                s.notes,
                count(c.id)::int AS channel_count
            FROM sensors s
            LEFT JOIN sensor_channels c ON c.sensor_id = s.id
            GROUP BY s.id, s.name, s.location, s.model, s.installed_on, s.notes
            ORDER BY s.name;
            """;

        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var sensors = new List<SensorSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sensors.Add(ReadSensorSummary(reader));
        }

        return sensors;
    }

    public async Task<SensorSummary?> GetSensorAsync(Guid sensorId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id,
                s.name,
                s.location,
                s.model,
                s.installed_on,
                s.notes,
                count(c.id)::int AS channel_count
            FROM sensors s
            LEFT JOIN sensor_channels c ON c.sensor_id = s.id
            WHERE s.id = @sensor_id
            GROUP BY s.id, s.name, s.location, s.model, s.installed_on, s.notes;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sensor_id", sensorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSensorSummary(reader)
            : null;
    }

    public async Task<IReadOnlyList<SensorChannelSummary>> GetChannelsAsync(Guid sensorId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                sensor_id,
                name,
                unit,
                extract(epoch FROM nominal_sample_interval)::int AS nominal_sample_seconds,
                expected_min,
                expected_max,
                alarm_normal_min,
                alarm_normal_max,
                alarm_downsample_method,
                is_enabled
            FROM sensor_channels
            WHERE sensor_id = @sensor_id
            ORDER BY name;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sensor_id", sensorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var channels = new List<SensorChannelSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            channels.Add(new SensorChannelSummary(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                new AlarmConfiguration(
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    ParseDownsampleMethod(reader.GetString(9))),
                reader.GetBoolean(10)));
        }

        return channels;
    }

    private static SensorSummary ReadSensorSummary(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateOnly>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6));

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
