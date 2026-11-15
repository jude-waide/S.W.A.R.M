using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DroneSwarm.Web;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddSingleton<GameSessionRegistry>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/plugin/status", (HttpContext context, GameSessionRegistry games) =>
    GetPluginStatus(GameSessionRegistry.Minecraft, context, games, app));
app.MapGet("/api/plugin/{game}/status", (string game, HttpContext context, GameSessionRegistry games) =>
    GetPluginStatus(game, context, games, app));

app.MapPost("/api/plugin/missions", (HttpContext context, GameSessionRegistry games) =>
    InitializeMissionAsync(GameSessionRegistry.Minecraft, context, games, app));
app.MapPost("/api/plugin/{game}/missions", (string game, HttpContext context, GameSessionRegistry games) =>
    InitializeMissionAsync(game, context, games, app));

app.MapPost("/api/plugin/missions/{missionId}/observations",
    (string missionId, HttpContext context, GameSessionRegistry games) =>
        AcceptFrameAsync(GameSessionRegistry.Minecraft, missionId, context, games, false, app));
app.MapPost("/api/plugin/missions/{missionId}/telemetry",
    (string missionId, HttpContext context, GameSessionRegistry games) =>
        AcceptFrameAsync(GameSessionRegistry.Minecraft, missionId, context, games, true, app));
app.MapPost("/api/plugin/{game}/missions/{missionId}/observations",
    (string game, string missionId, HttpContext context, GameSessionRegistry games) =>
        AcceptFrameAsync(game, missionId, context, games, false, app));
app.MapPost("/api/plugin/{game}/missions/{missionId}/telemetry",
    (string game, string missionId, HttpContext context, GameSessionRegistry games) =>
        AcceptFrameAsync(game, missionId, context, games, true, app));

app.MapDelete("/api/plugin/missions/{missionId}", (string missionId, HttpContext context, GameSessionRegistry games) =>
    ResetMissionAsync(GameSessionRegistry.Minecraft, missionId, context, games, app));
app.MapDelete("/api/plugin/{game}/missions/{missionId}",
    (string game, string missionId, HttpContext context, GameSessionRegistry games) =>
        ResetMissionAsync(game, missionId, context, games, app));

app.MapGet("/api/public/games", (GameSessionRegistry games) => Results.Ok(games.GetStatuses()));
app.MapGet("/api/public/snapshot", (GameSessionRegistry games) =>
    Results.Ok(games.GetMissionService(GameSessionRegistry.Minecraft).GetSnapshot()));
app.MapGet("/api/public/{game}/snapshot", (string game, GameSessionRegistry games) =>
    GetSnapshot(game, games));
app.MapGet("/api/public/replay", (GameSessionRegistry games) =>
    Results.Ok(games.GetMissionService(GameSessionRegistry.Minecraft).GetReplayIndex()));
app.MapGet("/api/public/{game}/replay", (string game, GameSessionRegistry games) =>
    GetReplayIndex(game, games));
app.MapGet("/api/public/replay/{missionId}/frames/{frame:int}",
    (string missionId, int frame, GameSessionRegistry games) =>
        GetReplayFrame(GameSessionRegistry.Minecraft, missionId, frame, games));
app.MapGet("/api/public/{game}/replay/{missionId}/frames/{frame:int}",
    (string game, string missionId, int frame, GameSessionRegistry games) =>
        GetReplayFrame(game, missionId, frame, games));
app.MapGet("/api/public/drones/{droneId}", (string droneId, GameSessionRegistry games) =>
    GetDroneDebug(GameSessionRegistry.Minecraft, droneId, games));
app.MapGet("/api/public/{game}/drones/{droneId}", (string game, string droneId, GameSessionRegistry games) =>
    GetDroneDebug(game, droneId, games));
app.MapHub<MissionHub>("/hubs/mission");
app.MapHub<MissionHub>("/hubs/mission/{game}");
app.MapFallbackToFile("index.html");

app.Run();

static async Task<string> ReadBodyAsync(HttpRequest request)
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
    return await reader.ReadToEndAsync();
}

static string Hash(string body) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

static IResult GetPluginStatus(string game, HttpContext context, GameSessionRegistry games, WebApplication app)
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    try
    {
        return Results.Ok(games.GetMissionService(game).GetPluginStatus());
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
}

static async Task<IResult> InitializeMissionAsync(
    string game, HttpContext context, GameSessionRegistry games, WebApplication app)
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    var rawBody = await ReadBodyAsync(context.Request);
    try
    {
        var normalizedGame = games.NormalizeGameId(game);
        var missions = games.GetMissionService(normalizedGame);
        var body = JsonSerializer.Deserialize<InitializeMissionRequest>(rawBody, JsonOptions.Default)
            ?? throw new ProtocolException(400, "invalid_json", "Request body must be a JSON object.");
        var result = missions.Initialize(body, Hash(rawBody));
        await PublishMissionUpdateAsync(normalizedGame, missions, app.Services);
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
}

static async Task<IResult> AcceptFrameAsync(
    string game, string missionId, HttpContext context, GameSessionRegistry games, bool motionOnly, WebApplication app)
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    var rawBody = await ReadBodyAsync(context.Request);
    try
    {
        var normalizedGame = games.NormalizeGameId(game);
        var missions = games.GetMissionService(normalizedGame);
        var body = JsonSerializer.Deserialize<ObservationFrame>(rawBody, JsonOptions.Default)
            ?? throw new ProtocolException(400, "invalid_json", "Request body must be a JSON object.");
        var result = missions.AcceptFrame(missionId, body, Hash(rawBody), motionOnly);
        if (motionOnly)
            await app.Services.GetRequiredService<IHubContext<MissionHub>>().Clients
                .Group(MissionHub.GameGroup(normalizedGame))
                .SendAsync("motionUpdated", missions.GetMotionSnapshot());
        else
            await PublishMissionUpdateAsync(normalizedGame, missions, app.Services);
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
}

static async Task<IResult> ResetMissionAsync(
    string game, string missionId, HttpContext context, GameSessionRegistry games, WebApplication app)
{
    if (!PluginAuthentication.IsAuthorized(context, app.Configuration["PLUGIN_KEY"]))
        return PluginAuthentication.Unauthorized();

    try
    {
        var normalizedGame = games.NormalizeGameId(game);
        var missions = games.GetMissionService(normalizedGame);
        missions.Reset(missionId);
        await PublishMissionUpdateAsync(normalizedGame, missions, app.Services);
        return Results.NoContent();
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
}

static IResult GetSnapshot(string game, GameSessionRegistry games)
{
    try
    {
        return Results.Ok(games.GetMissionService(game).GetSnapshot());
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
}

static IResult GetReplayIndex(string game, GameSessionRegistry games)
{
    try
    {
        return Results.Ok(games.GetMissionService(game).GetReplayIndex());
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
}

static IResult GetReplayFrame(string game, string missionId, int frame, GameSessionRegistry games)
{
    try
    {
        return Results.Ok(games.GetMissionService(game).GetReplayFrame(missionId, frame));
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
}

static IResult GetDroneDebug(string game, string droneId, GameSessionRegistry games)
{
    try
    {
        return Results.Ok(games.GetMissionService(game).GetDroneDebug(droneId));
    }
    catch (ProtocolException exception)
    {
        return ProtocolError(exception);
    }
}

static async Task PublishMissionUpdateAsync(string game, MissionService missions, IServiceProvider services)
{
    var clients = services.GetRequiredService<IHubContext<MissionHub>>().Clients;
    var gameClients = clients.Group(MissionHub.GameGroup(game));
    var snapshot = missions.GetSnapshot();
    await gameClients.SendAsync("missionUpdated", snapshot);
    var roster = snapshot.Drones.Select(drone => drone.Id).ToHashSet(StringComparer.Ordinal);
    foreach (var droneId in MissionHub.SubscribedDroneIds(game).Where(roster.Contains))
        await clients.Group(MissionHub.DroneGroup(game, droneId))
            .SendAsync("droneDebugUpdated", missions.GetDroneDebug(droneId));
}

static IResult ProtocolError(ProtocolException exception) => Results.Problem(
    statusCode: exception.StatusCode,
    title: exception.Message,
    extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

public partial class Program { }
