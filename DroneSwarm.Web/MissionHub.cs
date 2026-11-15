using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace DroneSwarm.Web;

public sealed class MissionHub(GameSessionRegistry sessions) : Hub
{
    private static readonly ConcurrentDictionary<(string Game, string Drone), ConcurrentDictionary<string, byte>> Subscribers = new();

    private string Game => sessions.NormalizeGameId(
        Context.GetHttpContext()?.Request.RouteValues["game"]?.ToString() ?? GameSessionRegistry.Minecraft);

    public async Task SubscribeDrone(string droneId)
    {
        var game = Game;
        var debug = sessions.GetMissionService(game).GetDroneDebug(droneId);
        await Groups.AddToGroupAsync(Context.ConnectionId, DroneGroup(game, droneId));
        Subscribers.GetOrAdd((game, droneId), _ => new(StringComparer.Ordinal))[Context.ConnectionId] = 0;
        await Clients.Caller.SendAsync("droneDebugUpdated", debug);
    }

    public async Task UnsubscribeDrone(string droneId)
    {
        var game = Game;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, DroneGroup(game, droneId));
        RemoveSubscriber(game, droneId, Context.ConnectionId);
    }

    public Task RequestSnapshot() =>
        Clients.Caller.SendAsync("missionUpdated", sessions.GetMissionService(Game).GetSnapshot());

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GameGroup(Game));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var (game, droneId) in Subscribers.Keys)
            RemoveSubscriber(game, droneId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    internal static string GameGroup(string game) => $"game:{game}";
    internal static string DroneGroup(string game, string droneId) => $"{GameGroup(game)}:drone:{droneId}";

    internal static IEnumerable<string> SubscribedDroneIds(string game) =>
        Subscribers.Keys.Where(key => key.Game == game).Select(key => key.Drone);

    private static void RemoveSubscriber(string game, string droneId, string connectionId)
    {
        var key = (game, droneId);
        if (!Subscribers.TryGetValue(key, out var connections)) return;
        connections.TryRemove(connectionId, out _);
        if (connections.IsEmpty)
            Subscribers.TryRemove(new KeyValuePair<(string Game, string Drone), ConcurrentDictionary<string, byte>>(key, connections));
    }
}
