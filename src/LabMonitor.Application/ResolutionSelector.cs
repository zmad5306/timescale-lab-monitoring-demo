using LabMonitor.Domain;

namespace LabMonitor.Application;

public sealed record ResolutionSelection(
    TelemetryResolution Resolution,
    TimeSpan? BucketSize,
    string SourceName,
    int TargetPointCount);

public interface IResolutionSelector
{
    ResolutionSelection Select(DateTimeOffset from, DateTimeOffset to, int chartWidth);
}

public sealed class ResolutionSelector : IResolutionSelector
{
    private const int MinTargetPoints = 800;
    private const int MaxTargetPoints = 1500;

    public ResolutionSelection Select(DateTimeOffset from, DateTimeOffset to, int chartWidth)
    {
        if (to <= from)
        {
            throw new ArgumentException("The query end time must be after the start time.");
        }

        var targetPointCount = Math.Clamp(chartWidth, MinTargetPoints, MaxTargetPoints);
        var visibleRange = to - from;
        var targetBucket = visibleRange / targetPointCount;

        if (visibleRange <= TimeSpan.FromHours(6) && targetBucket < TimeSpan.FromMinutes(5))
        {
            return new(TelemetryResolution.Raw, null, "sensor_readings", targetPointCount);
        }

        if (targetBucket < TimeSpan.FromHours(1))
        {
            return new(TelemetryResolution.FiveMinutes, TimeSpan.FromMinutes(5), "sensor_readings_5m", targetPointCount);
        }

        if (targetBucket < TimeSpan.FromDays(1))
        {
            return new(TelemetryResolution.OneHour, TimeSpan.FromHours(1), "sensor_readings_1h", targetPointCount);
        }

        if (targetBucket < TimeSpan.FromDays(30))
        {
            return new(TelemetryResolution.OneDay, TimeSpan.FromDays(1), "sensor_readings_1d", targetPointCount);
        }

        return new(TelemetryResolution.OneMonth, TimeSpan.FromDays(30), "sensor_readings_1mo", targetPointCount);
    }
}
