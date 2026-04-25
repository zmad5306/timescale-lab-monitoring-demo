namespace LabMonitor.Domain;

public sealed record Sensor(
    Guid Id,
    string Name,
    string Location,
    string Model,
    DateOnly InstalledOn,
    string? Notes);
