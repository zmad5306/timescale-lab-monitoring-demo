using LabMonitor.Domain;

namespace LabMonitor.Application;

public sealed record SensorSeriesQuery(
    Guid SensorId,
    IReadOnlyList<Guid> ChannelIds,
    DateTimeOffset From,
    DateTimeOffset To,
    int ChartWidth);

public sealed record SensorSeriesResponse(
    Guid SensorId,
    DateTimeOffset From,
    DateTimeOffset To,
    TelemetryResolution Resolution,
    int? BucketSeconds,
    string Source,
    int TargetPointCount,
    IReadOnlyList<ChannelSeries> Series);

public sealed record ChannelSeries(
    Guid ChannelId,
    string Name,
    string Unit,
    IReadOnlyList<double[]> Points);

public interface ITimeSeriesQueryService
{
    Task<SensorSeriesResponse> QuerySensorSeriesAsync(
        SensorSeriesQuery query,
        CancellationToken cancellationToken);
}
