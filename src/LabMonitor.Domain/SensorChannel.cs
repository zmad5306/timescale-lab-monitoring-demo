namespace LabMonitor.Domain;

public sealed record SensorChannel(
    Guid Id,
    Guid SensorId,
    string Name,
    string Unit,
    TimeSpan NominalSampleInterval,
    double? ExpectedMin,
    double? ExpectedMax,
    AlarmConfiguration Alarm,
    bool IsEnabled,
    string? GeneratorProfile);

public sealed record AlarmConfiguration
{
    public AlarmConfiguration(double? normalMin, double? normalMax, DownsampleMethod downsampleMethod)
    {
        if (normalMin is null && normalMax is null)
        {
            throw new ArgumentException("Alarm configuration must define at least one normal range boundary.");
        }

        if (normalMin is not null && normalMax is not null && normalMax <= normalMin)
        {
            throw new ArgumentException("Alarm normal maximum must be greater than the normal minimum.");
        }

        NormalMin = normalMin;
        NormalMax = normalMax;
        DownsampleMethod = downsampleMethod;
    }

    public double? NormalMin { get; }

    public double? NormalMax { get; }

    public DownsampleMethod DownsampleMethod { get; }
}

public enum DownsampleMethod
{
    Average,
    Minimum,
    Maximum,
    First,
    Last
}
