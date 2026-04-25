using LabMonitor.Application;
using LabMonitor.Domain;

namespace LabMonitor.Infrastructure;

public sealed class InMemorySensorCatalogService : ISensorCatalogService
{
    private static readonly Guid LabSuiteA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ColdRoom = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid VibrationBench = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IReadOnlyList<Sensor> sensors =
    [
        new(LabSuiteA, "Lab Suite A Environmental Monitor", "Lab Suite A", "EnviroSense X4", new DateOnly(2019, 2, 14), "Primary environmental monitor for bench area."),
        new(ColdRoom, "Cold Room Monitor", "Cold Storage 2", "CryoWatch C6", new DateOnly(2018, 7, 3), "Tracks cold room probes and door activity."),
        new(VibrationBench, "Vibration Isolation Bench", "Analytical Lab", "MotionGuard V2", new DateOnly(2020, 9, 22), "Monitors vibration-sensitive equipment bench.")
    ];

    private readonly IReadOnlyList<SensorChannel> channels =
    [
        new(Guid.Parse("aaaaaaaa-0001-0000-0000-000000000001"), LabSuiteA, "Ambient Temperature", "degC", TimeSpan.FromMinutes(1), 15, 30, true, "ambient-temperature"),
        new(Guid.Parse("aaaaaaaa-0002-0000-0000-000000000002"), LabSuiteA, "Relative Humidity", "%RH", TimeSpan.FromMinutes(1), 20, 80, true, "relative-humidity"),
        new(Guid.Parse("aaaaaaaa-0003-0000-0000-000000000003"), LabSuiteA, "CO2 Concentration", "ppm", TimeSpan.FromMinutes(5), 350, 2000, true, "co2-work-hours"),
        new(Guid.Parse("aaaaaaaa-0004-0000-0000-000000000004"), LabSuiteA, "Pressure Differential", "Pa", TimeSpan.FromMinutes(1), -20, 50, true, "pressure-differential"),

        new(Guid.Parse("bbbbbbbb-0001-0000-0000-000000000001"), ColdRoom, "Freezer Probe A", "degC", TimeSpan.FromMinutes(1), -35, -10, true, "freezer-probe"),
        new(Guid.Parse("bbbbbbbb-0002-0000-0000-000000000002"), ColdRoom, "Freezer Probe B", "degC", TimeSpan.FromMinutes(1), -35, -10, true, "freezer-probe"),
        new(Guid.Parse("bbbbbbbb-0003-0000-0000-000000000003"), ColdRoom, "Door Open Count", "count", TimeSpan.FromMinutes(5), 0, 10, true, "door-open-count"),

        new(Guid.Parse("cccccccc-0001-0000-0000-000000000001"), VibrationBench, "Vibration RMS", "g", TimeSpan.FromSeconds(5), 0, 0.5, true, "vibration-rms"),
        new(Guid.Parse("cccccccc-0002-0000-0000-000000000002"), VibrationBench, "Shock Event Magnitude", "g", TimeSpan.FromSeconds(5), 0, 5, true, "shock-event"),
        new(Guid.Parse("cccccccc-0003-0000-0000-000000000003"), VibrationBench, "Bench Temperature", "degC", TimeSpan.FromMinutes(1), 15, 30, true, "ambient-temperature")
    ];

    public IReadOnlyList<SensorSummary> GetSensors() =>
        sensors
            .Select(sensor => SensorCatalogMapping.ToSummary(sensor, channels.Count(channel => channel.SensorId == sensor.Id)))
            .OrderBy(sensor => sensor.Name)
            .ToArray();

    public SensorSummary? GetSensor(Guid sensorId)
    {
        var sensor = sensors.SingleOrDefault(item => item.Id == sensorId);
        return sensor is null
            ? null
            : SensorCatalogMapping.ToSummary(sensor, channels.Count(channel => channel.SensorId == sensor.Id));
    }

    public IReadOnlyList<SensorChannelSummary> GetChannels(Guid sensorId) =>
        channels
            .Where(channel => channel.SensorId == sensorId)
            .OrderBy(channel => channel.Name)
            .Select(SensorCatalogMapping.ToSummary)
            .ToArray();
}
