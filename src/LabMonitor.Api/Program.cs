using LabMonitor.Application;
using LabMonitor.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("LabMonitor")
        ?? "Host=localhost;Port=5432;Database=labmonitor;Username=labmonitor;Password=labmonitor";

    return new NpgsqlDataSourceBuilder(connectionString).Build();
});
builder.Services.AddScoped<ISensorCatalogService, PostgresSensorCatalogService>();
builder.Services.AddScoped<ITimeSeriesQueryService, PostgresTimeSeriesQueryService>();
builder.Services.AddSingleton<IResolutionSelector, ResolutionSelector>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "lab-monitor-api",
    time = DateTimeOffset.UtcNow
}));

api.MapGet("/sensors", async (ISensorCatalogService catalog, CancellationToken cancellationToken) =>
    Results.Ok(await catalog.GetSensorsAsync(cancellationToken)));

api.MapGet("/sensors/{sensorId:guid}", async (Guid sensorId, ISensorCatalogService catalog, CancellationToken cancellationToken) =>
{
    var sensor = await catalog.GetSensorAsync(sensorId, cancellationToken);
    return sensor is null ? Results.NotFound() : Results.Ok(sensor);
});

api.MapGet("/sensors/{sensorId:guid}/channels", async (Guid sensorId, ISensorCatalogService catalog, CancellationToken cancellationToken) =>
{
    if (await catalog.GetSensorAsync(sensorId, cancellationToken) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(await catalog.GetChannelsAsync(sensorId, cancellationToken));
});

api.MapGet("/sensors/{sensorId:guid}/reading-range", async (
    Guid sensorId,
    ISensorCatalogService catalog,
    ITimeSeriesQueryService timeSeries,
    CancellationToken cancellationToken) =>
{
    if (await catalog.GetSensorAsync(sensorId, cancellationToken) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(await timeSeries.GetSensorReadingRangeAsync(sensorId, cancellationToken));
});

api.MapGet("/sensors/{sensorId:guid}/series", async (
    Guid sensorId,
    DateTimeOffset from,
    DateTimeOffset to,
    int? width,
    string? channelIds,
    ISensorCatalogService catalog,
    ITimeSeriesQueryService timeSeries,
    CancellationToken cancellationToken) =>
{
    if (await catalog.GetSensorAsync(sensorId, cancellationToken) is null)
    {
        return Results.NotFound();
    }

    if (!TryParseChannelIds(channelIds, out var parsedChannelIds))
    {
        return Results.BadRequest(new
        {
            error = "channelIds must be a comma-separated list of GUIDs."
        });
    }

    var response = await timeSeries.QuerySensorSeriesAsync(
        new SensorSeriesQuery(sensorId, parsedChannelIds, from, to, width.GetValueOrDefault(1200)),
        cancellationToken);

    return Results.Ok(response);
});

api.MapGet("/resolution", (
    DateTimeOffset from,
    DateTimeOffset to,
    int? width,
    IResolutionSelector selector) =>
{
    var selection = selector.Select(from, to, width.GetValueOrDefault(1200));

    return Results.Ok(new
    {
        resolution = selection.Resolution.ToString(),
        bucketSeconds = selection.BucketSize is null ? null : (int?)selection.BucketSize.Value.TotalSeconds,
        source = selection.SourceName,
        targetPointCount = selection.TargetPointCount
    });
});

app.Run();

static bool TryParseChannelIds(string? value, out IReadOnlyList<Guid> channelIds)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        channelIds = [];
        return true;
    }

    var parsed = new List<Guid>();
    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Guid.TryParse(item, out var channelId))
        {
            channelIds = [];
            return false;
        }

        parsed.Add(channelId);
    }

    channelIds = parsed;
    return true;
}
