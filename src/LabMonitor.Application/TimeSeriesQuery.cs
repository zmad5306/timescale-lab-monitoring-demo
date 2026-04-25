using LabMonitor.Domain;

namespace LabMonitor.Application;

public sealed record SensorSeriesQuery(
    Guid SensorId,
    IReadOnlyList<Guid> ChannelIds,
    DateTimeOffset From,
    DateTimeOffset To,
    int ChartWidth,
    IReadOnlyDictionary<Guid, DownsampleMethod> DownsampleMethods);

public sealed record SensorSeriesResponse(
    Guid SensorId,
    DateTimeOffset From,
    DateTimeOffset To,
    TelemetryResolution Resolution,
    int? BucketSeconds,
    string Source,
    int TargetPointCount,
    IReadOnlyList<ChannelSeries> Series);

public sealed record SensorReadingRangeResponse(
    Guid SensorId,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record ChannelSeries(
    Guid ChannelId,
    string Name,
    string Unit,
    DownsampleMethod DownsampleMethod,
    IReadOnlyList<double[]> Points);

public enum DownsampleMethod
{
    Average,
    Minimum,
    Maximum,
    First,
    Last
}

public interface ITimeSeriesQueryService
{
    Task<SensorSeriesResponse> QuerySensorSeriesAsync(
        SensorSeriesQuery query,
        CancellationToken cancellationToken);

    Task<SensorReadingRangeResponse> GetSensorReadingRangeAsync(
        Guid sensorId,
        CancellationToken cancellationToken);
}
