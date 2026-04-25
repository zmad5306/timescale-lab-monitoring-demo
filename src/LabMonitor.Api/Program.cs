using LabMonitor.Application;
using LabMonitor.Infrastructure;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;

const int ClientClosedRequestStatusCode = 499;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
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

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = ClientClosedRequestStatusCode;
        }
    }
});

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
    string? downsample,
    ISensorCatalogService catalog,
    ITimeSeriesQueryService timeSeries,
    CancellationToken cancellationToken) =>
{
    if (!HasValidRange(from, to))
    {
        return Results.BadRequest(new
        {
            error = "to must be after from."
        });
    }

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

    if (!TryParseDownsampleMethods(downsample, out var downsampleMethods))
    {
        return Results.BadRequest(new
        {
            error = "downsample must be a comma-separated list of channelId:method pairs. Methods: average, minimum, maximum, first, last."
        });
    }

    if (parsedChannelIds.Count > 0 && downsampleMethods.Keys.Any(channelId => !parsedChannelIds.Contains(channelId)))
    {
        return Results.BadRequest(new
        {
            error = "downsample contains a channel ID that is not included in channelIds."
        });
    }

    var response = await timeSeries.QuerySensorSeriesAsync(
        new SensorSeriesQuery(sensorId, parsedChannelIds, from, to, width.GetValueOrDefault(1200), downsampleMethods),
        cancellationToken);

    return Results.Ok(response);
});

api.MapGet("/resolution", (
    DateTimeOffset from,
    DateTimeOffset to,
    int? width,
    IResolutionSelector selector) =>
{
    if (!HasValidRange(from, to))
    {
        return Results.BadRequest(new
        {
            error = "to must be after from."
        });
    }

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

static bool HasValidRange(DateTimeOffset from, DateTimeOffset to) =>
    to > from;

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

static bool TryParseDownsampleMethods(string? value, out IReadOnlyDictionary<Guid, DownsampleMethod> methods)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        methods = new Dictionary<Guid, DownsampleMethod>();
        return true;
    }

    var parsed = new Dictionary<Guid, DownsampleMethod>();
    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var separatorIndex = item.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == item.Length - 1)
        {
            methods = new Dictionary<Guid, DownsampleMethod>();
            return false;
        }

        if (!Guid.TryParse(item[..separatorIndex], out var channelId)
            || !TryParseDownsampleMethod(item[(separatorIndex + 1)..], out var method))
        {
            methods = new Dictionary<Guid, DownsampleMethod>();
            return false;
        }

        parsed[channelId] = method;
    }

    methods = parsed;
    return true;
}

static bool TryParseDownsampleMethod(string value, out DownsampleMethod method)
{
    switch (value.Trim().ToLowerInvariant())
    {
        case "average":
        case "avg":
            method = DownsampleMethod.Average;
            return true;
        case "minimum":
        case "min":
            method = DownsampleMethod.Minimum;
            return true;
        case "maximum":
        case "max":
            method = DownsampleMethod.Maximum;
            return true;
        case "first":
            method = DownsampleMethod.First;
            return true;
        case "last":
            method = DownsampleMethod.Last;
            return true;
        default:
            method = default;
            return false;
    }
}
