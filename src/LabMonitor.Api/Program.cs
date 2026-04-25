using LabMonitor.Application;
using LabMonitor.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<ISensorCatalogService, InMemorySensorCatalogService>();
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

api.MapGet("/sensors", (ISensorCatalogService catalog) =>
    Results.Ok(catalog.GetSensors()));

api.MapGet("/sensors/{sensorId:guid}", (Guid sensorId, ISensorCatalogService catalog) =>
{
    var sensor = catalog.GetSensor(sensorId);
    return sensor is null ? Results.NotFound() : Results.Ok(sensor);
});

api.MapGet("/sensors/{sensorId:guid}/channels", (Guid sensorId, ISensorCatalogService catalog) =>
{
    if (catalog.GetSensor(sensorId) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(catalog.GetChannels(sensorId));
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
