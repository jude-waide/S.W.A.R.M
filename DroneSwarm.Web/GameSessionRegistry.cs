namespace DroneSwarm.Web;

public sealed record GameSessionStatus(string Game, PluginStatus Mission);

public sealed class GameSessionRegistry
{
    public const string Minecraft = "minecraft";
    public const string Skyrim = "skyrim";

    private static readonly string[] GameIds = [Minecraft, Skyrim];
    private readonly Dictionary<string, MissionService> _missions = new(StringComparer.OrdinalIgnoreCase)
    {
        [Minecraft] = new(),
        [Skyrim] = new()
    };

    public MissionService GetMissionService(string game)
    {
        if (_missions.TryGetValue(game, out var missions))
            return missions;
        throw new ProtocolException(404, "game_not_found", $"Game '{game}' is not supported.");
    }

    public string NormalizeGameId(string game)
    {
        if (_missions.ContainsKey(game))
            return GameIds.First(id => string.Equals(id, game, StringComparison.OrdinalIgnoreCase));
        throw new ProtocolException(404, "game_not_found", $"Game '{game}' is not supported.");
    }

    public IReadOnlyList<GameSessionStatus> GetStatuses() =>
        GameIds.Select(game => new GameSessionStatus(game, _missions[game].GetPluginStatus())).ToArray();
}
