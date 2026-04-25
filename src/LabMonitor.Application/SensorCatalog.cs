using LabMonitor.Domain;

namespace LabMonitor.Application;

public sealed record SensorSummary(
    Guid Id,
    string Name,
    string Location,
    string Model,
    DateOnly InstalledOn,
    string? Notes,
    int ChannelCount);

public sealed record SensorChannelSummary(
    Guid Id,
    Guid SensorId,
    string Name,
    string Unit,
    int NominalSampleSeconds,
    double? ExpectedMin,
    double? ExpectedMax,
    bool IsEnabled);

public interface ISensorCatalogService
{
    Task<IReadOnlyList<SensorSummary>> GetSensorsAsync(CancellationToken cancellationToken);

    Task<SensorSummary?> GetSensorAsync(Guid sensorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorChannelSummary>> GetChannelsAsync(Guid sensorId, CancellationToken cancellationToken);
}

public static class SensorCatalogMapping
{
    public static SensorSummary ToSummary(Sensor sensor, int channelCount) =>
        new(
            sensor.Id,
            sensor.Name,
            sensor.Location,
            sensor.Model,
            sensor.InstalledOn,
            sensor.Notes,
            channelCount);

    public static SensorChannelSummary ToSummary(SensorChannel channel) =>
        new(
            channel.Id,
            channel.SensorId,
            channel.Name,
            channel.Unit,
            (int)channel.NominalSampleInterval.TotalSeconds,
            channel.ExpectedMin,
            channel.ExpectedMax,
            channel.IsEnabled);
}
