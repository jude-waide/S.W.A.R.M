using Xunit;

namespace DroneSwarm.Web.Tests;

public sealed class GameSessionRegistryTests
{
    [Fact]
    public void MinecraftAndSkyrimMissionsRunAndResetIndependently()
    {
        var sessions = new GameSessionRegistry();
        var minecraftId = Guid.NewGuid().ToString();
        var skyrimId = Guid.NewGuid().ToString();
        var minecraft = sessions.GetMissionService(GameSessionRegistry.Minecraft);
        var skyrim = sessions.GetMissionService(GameSessionRegistry.Skyrim);

        Initialize(minecraft, minecraftId, "minecraft-world");
        Initialize(skyrim, skyrimId, "skyrim-world");

        Assert.Equal(minecraftId, minecraft.GetSnapshot().MissionId);
        Assert.Equal(skyrimId, skyrim.GetSnapshot().MissionId);

        minecraft.Reset(minecraftId);

        Assert.Null(minecraft.GetSnapshot().MissionId);
        Assert.Equal(skyrimId, skyrim.GetSnapshot().MissionId);
    }

    [Fact]
    public void UnknownGameIsRejectedExplicitly()
    {
        var sessions = new GameSessionRegistry();

        var exception = Assert.Throws<ProtocolException>(() => sessions.GetMissionService("fallout"));

        Assert.Equal("game_not_found", exception.Code);
    }

    private static void Initialize(MissionService missions, string missionId, string worldId)
    {
        var position = new DronePosition { X = 0.5, Y = 20.5, Z = 0.5 };
        var cells = new List<CellObservation>();
        for (var x = -3; x <= 3; x++)
        for (var y = 17; y <= 23; y++)
        for (var z = -3; z <= 3; z++)
            cells.Add(new() { X = x, Y = y, Z = z, Solid = false });

        missions.Initialize(new()
        {
            ProtocolVersion = 1,
            MissionId = missionId,
            WorldId = worldId,
            BoundsMin = new() { X = -64, Y = 0, Z = -64 },
            Tower = new() { X = 0, Y = 20, Z = 0 },
            Target = new() { X = 40, Y = 20, Z = 40 },
            InitialFrame = new()
            {
                Sequence = 0,
                GameTick = 0,
                Drones =
                [
                    new()
                    {
                        DroneId = "SCOUT-01",
                        Availability = "available",
                        Position = position,
                        Cells = cells
                    }
                ]
            }
        }, missionId);
    }
}
