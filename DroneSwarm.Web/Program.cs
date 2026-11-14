using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DroneSwarm.Web;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddSingleton<MissionService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/plugin/status", (HttpContext context, MissionService missions) =>
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    return Results.Ok(missions.GetPluginStatus());
});

app.MapPost("/api/plugin/missions", async (HttpContext context, MissionService missions) =>
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    var rawBody = await ReadBodyAsync(context.Request);
    try
    {
        var body = JsonSerializer.Deserialize<InitializeMissionRequest>(rawBody, JsonOptions.Default)
            ?? throw new ProtocolException(400, "invalid_json", "Request body must be a JSON object.");
        var result = missions.Initialize(body, Hash(rawBody));
        await PublishMissionUpdateAsync(missions, app.Services);
        return Results.Ok(result);
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
    catch (JsonException)
    {
        return ProtocolError(new ProtocolException(400, "invalid_json", "Request body is not valid for this endpoint."));
    }
});

app.MapPost("/api/plugin/missions/{missionId}/observations", async (
    string missionId, HttpContext context, MissionService missions) =>
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    var rawBody = await ReadBodyAsync(context.Request);
    try
    {
        var body = JsonSerializer.Deserialize<ObservationFrame>(rawBody, JsonOptions.Default)
            ?? throw new ProtocolException(400, "invalid_json", "Request body must be a JSON object.");
        var result = missions.AcceptFrame(missionId, body, Hash(rawBody));
        await PublishMissionUpdateAsync(missions, app.Services);
        return Results.Ok(result);
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
    catch (JsonException)
    {
        return ProtocolError(new ProtocolException(400, "invalid_json", "Request body is not valid for this endpoint."));
    }
});

app.MapDelete("/api/plugin/missions/{missionId}", async (
    string missionId, HttpContext context, MissionService missions) =>
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    try
    {
        missions.Reset(missionId);
        await PublishMissionUpdateAsync(missions, app.Services);
        return Results.NoContent();
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
});

app.MapGet("/api/public/snapshot", (MissionService missions) => Results.Ok(missions.GetSnapshot()));
app.MapGet("/api/public/drones/{droneId}", (string droneId, MissionService missions) =>
{
    try
    {
        return Results.Ok(missions.GetDroneDebug(droneId));
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
});
app.MapHub<MissionHub>("/hubs/mission");
app.MapFallbackToFile("index.html");

app.Run();

static async Task<string> ReadBodyAsync(HttpRequest request)
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
    return await reader.ReadToEndAsync();
}

static string Hash(string body) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

static async Task PublishMissionUpdateAsync(MissionService missions, IServiceProvider services)
{
    var clients = services.GetRequiredService<IHubContext<MissionHub>>().Clients;
    var snapshot = missions.GetSnapshot();
    await clients.All.SendAsync("missionUpdated", snapshot);
    var roster = snapshot.Drones.Select(drone => drone.Id).ToHashSet(StringComparer.Ordinal);
    foreach (var droneId in MissionHub.SubscribedDroneIds.Where(roster.Contains))
        await clients.Group(MissionHub.DroneGroup(droneId))
            .SendAsync("droneDebugUpdated", missions.GetDroneDebug(droneId));
}

static IResult ProtocolError(ProtocolException exception) => Results.Problem(
    statusCode: exception.StatusCode,
    title: exception.Message,
    extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

public partial class Program { }
