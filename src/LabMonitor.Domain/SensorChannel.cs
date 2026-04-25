namespace LabMonitor.Domain;

public sealed record SensorChannel(
    Guid Id,
    Guid SensorId,
    string Name,
    string Unit,
    TimeSpan NominalSampleInterval,
    double? ExpectedMin,
    double? ExpectedMax,
    bool IsEnabled,
    string? GeneratorProfile);
