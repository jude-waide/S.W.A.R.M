using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace DroneSwarm.Web;

public sealed class MissionHub(MissionService missions) : Hub
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Subscribers = new(StringComparer.Ordinal);

    public static IEnumerable<string> SubscribedDroneIds => Subscribers.Keys;

    public async Task SubscribeDrone(string droneId)
    {
        var debug = missions.GetDroneDebug(droneId);
        await Groups.AddToGroupAsync(Context.ConnectionId, DroneGroup(droneId));
        Subscribers.GetOrAdd(droneId, _ => new(StringComparer.Ordinal))[Context.ConnectionId] = 0;
        await Clients.Caller.SendAsync("droneDebugUpdated", debug);
    }

    public async Task UnsubscribeDrone(string droneId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, DroneGroup(droneId));
        RemoveSubscriber(droneId, Context.ConnectionId);
    }

    public Task RequestSnapshot() =>
        Clients.Caller.SendAsync("missionUpdated", missions.GetSnapshot());

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var droneId in Subscribers.Keys)
            RemoveSubscriber(droneId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    internal static string DroneGroup(string droneId) => $"drone:{droneId}";

    private static void RemoveSubscriber(string droneId, string connectionId)
    {
        if (!Subscribers.TryGetValue(droneId, out var connections)) return;
        connections.TryRemove(connectionId, out _);
        if (connections.IsEmpty)
            Subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(droneId, connections));
    }
}
