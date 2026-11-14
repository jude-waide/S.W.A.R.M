namespace DroneSwarm.Web.Tests;

public sealed class MissionCoordinationTests
{
    [Fact]
    public void OverallTargetIsVisibleBeforeDiscoveryWithoutChangingHiveOrScoutKnowledge()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        var target = new BlockPoint { X = 60, Y = 12, Z = 60 };
        Initialize(service, missionId, target, ["A"]);

        var snapshot = service.GetSnapshot();
        Assert.Equal(target, snapshot.OverallTarget);
        Assert.False(snapshot.TargetReported);
        Assert.Null(snapshot.ReportedTarget);
        Assert.Null(service.GetDroneDebug("A").KnownTarget);

        service.Reset(missionId);
        Assert.Null(service.GetSnapshot().OverallTarget);
    }

    [Fact]
    public void ScoutsStartingInTheSameCellReceiveDistinctDestinations()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B", "C"]);

        var response = Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("C", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));

        Assert.Equal(3, response.Commands.Count);
        Assert.Equal(3, response.Commands.Select(command => command.Destination).Distinct().Count());
    }

    [Fact]
    public void TrafficBlockedScoutWaitsWithoutDeclaringItsSearchExhausted()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B"]);
        static bool Corridor(int x, int y, int z) => x > 0 || y != 12 || z != 0;

        var response = Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, Corridor),
            Drone("B", new() { X = -0.5, Y = 12.5, Z = 0.5 }, Corridor));

        Assert.DoesNotContain(response.Commands, command => command.DroneId == "A");
        Assert.Equal("waitingForClearance", service.GetDroneDebug("A").Behavior);
        Assert.Equal("searching", service.GetSnapshot().Phase);
    }

    [Fact]
    public void MovementAvoidsOccupiedCellsAndUnfinishedDestinationsWhileScoutsKeepExploring()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B", "C"]);
        var positions = new Dictionary<string, DronePosition>
        {
            ["A"] = new() { X = 0.5, Y = 12.5, Z = 0.5 },
            ["B"] = new() { X = 1.5, Y = 12.5, Z = 0.5 },
            ["C"] = new() { X = 2.5, Y = 12.5, Z = 0.5 }
        };
        var pending = new Dictionary<string, MovementCommand>();
        var moves = 0;
        MovementCommand? heldCommand = null;
        for (var sequence = 1; sequence <= 65; sequence++)
        {
            var telemetry = new List<DroneTelemetry>();
            foreach (var (id, position) in positions.ToArray())
            {
                var drone = Drone(id, position, CellIsSolid);
                if (pending.TryGetValue(id, out var command) && !(id == "A" && sequence <= 5))
                {
                    var destination = command.Destination;
                    positions[id] = new() { X = destination.X + 0.5, Y = destination.Y + 0.5, Z = destination.Z + 0.5 };
                    drone = Drone(id, positions[id], CellIsSolid);
                    drone.CommandResult = new() { CommandId = command.CommandId, Status = "completed" };
                    moves++;
                }
                telemetry.Add(drone);
            }

            var response = Accept(service, missionId, sequence, sequence * 10, [.. telemetry]);
            var occupied = positions.Values
                .Select(position => new BlockPoint { X = (int)Math.Floor(position.X), Y = (int)Math.Floor(position.Y), Z = (int)Math.Floor(position.Z) })
                .ToHashSet();
            Assert.Equal(positions.Count, occupied.Count);
            Assert.Equal(response.Commands.Count, response.Commands.Select(command => command.Destination).Distinct().Count());
            Assert.All(response.Commands, command => Assert.DoesNotContain(command.Destination, occupied));
            pending = response.Commands.ToDictionary(command => command.DroneId);
            if (sequence == 1) heldCommand = pending["A"];
            if (sequence is >= 2 and <= 5) Assert.Equal(heldCommand, pending["A"]);
        }
        Assert.True(moves > 100, "Avoiding collisions must not stop the swarm from exploring.");
    }

    [Fact]
    public void TowerReachabilityAndKnowledgeRelayAdvanceOneHopPerCycle()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B"]);

        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 3.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));

        var first = service.GetSnapshot();
        var relayDrone = first.Drones.Single(drone => drone.Id == "B");
        Assert.True(relayDrone.ConnectedToTower);
        Assert.False(relayDrone.DirectlyConnectedToTower);
        Assert.False(relayDrone.PositionCurrent);
        Assert.Equal(0.5, relayDrone.Position!.X);
        Assert.DoesNotContain(first.Terrain, cell => cell.X == 8 && cell.Y == 12 && cell.Z == 0);
        Assert.Contains(first.LiveTerrain, cell => cell.X == 8 && cell.Y == 12 && cell.Z == 0);

        Accept(service, missionId, 2, 20,
            Drone("A", new() { X = 3.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));

        var second = service.GetSnapshot();
        relayDrone = second.Drones.Single(drone => drone.Id == "B");
        Assert.Equal(6.5, relayDrone.Position!.X);
        Assert.False(relayDrone.PositionCurrent);
        Assert.Contains(second.Terrain, cell => cell.X == 8 && cell.Y == 12 && cell.Z == 0);
    }

    [Fact]
    public void LiveTerrainMergesIsolatedDiscoveriesAndRemovesSolidsWhenNewerAirIsObserved()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B"]);
        static bool WithObstacle(int x, int y, int z) =>
            CellIsSolid(x, y, z) || (x == 14 && y == 12 && z == 0);

        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 12.5, Y = 12.5, Z = 0.5 }, WithObstacle),
            Drone("B", new() { X = 13.5, Y = 12.5, Z = 0.5 }, WithObstacle));

        var snapshot = service.GetSnapshot();
        Assert.All(snapshot.Drones, drone => Assert.False(drone.ConnectedToTower));
        Assert.DoesNotContain(snapshot.Terrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
        var obstacle = Assert.Single(snapshot.LiveTerrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
        Assert.Equal(10, obstacle.SourceTick);

        Accept(service, missionId, 2, 20,
            Drone("A", new() { X = 12.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Unavailable("B"));

        snapshot = service.GetSnapshot();
        Assert.DoesNotContain(snapshot.LiveTerrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
        Assert.DoesNotContain(snapshot.Terrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
    }

    [Fact]
    public void LivePositionsTrackIsolatedScoutsWithoutUpdatingTowerKnowledge()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A"]);

        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 12.5, Y = 12.5, Z = 0.5 }, CellIsSolid));

        var scout = Assert.Single(service.GetSnapshot().Drones);
        Assert.False(scout.ConnectedToTower);
        Assert.False(scout.PositionCurrent);
        Assert.Equal(0.5, scout.Position!.X);
        Assert.Equal(12.5, scout.LivePosition!.X);
        Assert.True(scout.LivePositionCurrent);

        Accept(service, missionId, 2, 20, Unavailable("A"));

        scout = Assert.Single(service.GetSnapshot().Drones);
        Assert.Equal(12.5, scout.LivePosition!.X);
        Assert.False(scout.LivePositionCurrent);
        Assert.True(scout.Faulted);
        Assert.Equal(0.5, scout.Position!.X);
    }

    [Fact]
    public void TowerAssignsAnAvailableDroneToSearchForAFaultedPeer()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 2, Y = 12, Z = 0 }, ["A", "B"]);

        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            new()
            {
                DroneId = "B",
                Availability = "unavailable",
                Reason = "Entity unavailable"
            });

        Assert.Equal("recovering", service.GetSnapshot().Phase);
        Assert.Contains("B", service.GetSnapshot().MissingDroneIds);

        Accept(service, missionId, 2, 20,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            new()
            {
                DroneId = "B",
                Availability = "unavailable",
                Reason = "Entity unavailable"
            });

        var debug = service.GetDroneDebug("A");
        Assert.Equal("B", debug.RecoveryTargetId);
        Assert.Equal("searchingForPeer", debug.Behavior);
    }

    [Fact]
    public void RecoveryRemainsIncompleteWhenEveryDroneIsFaulted()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 2, Y = 12, Z = 0 }, ["A", "B"]);

        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Unavailable("B"));
        Assert.True(service.GetSnapshot().TargetReported);

        Accept(service, missionId, 2, 20, Unavailable("A"), Unavailable("B"));

        var snapshot = service.GetSnapshot();
        Assert.Equal("recoveryIncomplete", snapshot.Phase);
        Assert.Equal(0, snapshot.RecoveredCount);
        Assert.Equal(["A", "B"], snapshot.MissingDroneIds);
    }

    [Fact]
    public void IsolatedDroneStartsAContactRendezvousAfterThirtySimulationSeconds()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B"]);

        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        for (var sequence = 2; sequence <= 60; sequence++)
        {
            Accept(service, missionId, sequence, sequence * 10,
                Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
                Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        }

        var debug = service.GetDroneDebug("B");
        Assert.Equal("A", debug.RendezvousTargetId);
        Assert.Equal(30, debug.LastContactAgeSeconds);
    }

    [Fact]
    public void HttpOutageDoesNotAdvanceTheIsolationWatchdog()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ["A", "B"]);
        Accept(service, missionId, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));

        Thread.Sleep(TimeSpan.FromMilliseconds(3100));
        Accept(service, missionId, 2, 610,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Accept(service, missionId, 3, 620,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));

        var debug = service.GetDroneDebug("B");
        Assert.Null(debug.RendezvousTargetId);
        Assert.Equal(1, debug.LastContactAgeSeconds);
    }

    private static void Initialize(MissionService service, string missionId, BlockPoint target, string[] droneIds)
    {
        service.Initialize(new()
        {
            ProtocolVersion = 1,
            MissionId = missionId,
            WorldId = "coordination-tests",
            BoundsMin = new() { X = -64, Y = 0, Z = -64 },
            Tower = new() { X = 0, Y = 12, Z = 0 },
            Target = target,
            InitialFrame = new()
            {
                Sequence = 0,
                GameTick = 0,
                Drones = droneIds
                    .Select(id => Drone(id, new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid))
                    .ToList()
            }
        }, "initial");
    }

    private static AcceptedFrameResponse Accept(
        MissionService service,
        string missionId,
        long sequence,
        long gameTick,
        params DroneTelemetry[] drones) =>
        service.AcceptFrame(missionId, new()
        {
            Sequence = sequence,
            GameTick = gameTick,
            Drones = [.. drones]
        }, $"frame-{sequence}");

    private static DroneTelemetry Drone(
        string id,
        DronePosition position,
        Func<int, int, int, bool> solid) => new()
    {
        DroneId = id,
        Availability = "available",
        Position = position,
        Cells = Cells(position, solid)
    };

    private static DroneTelemetry Unavailable(string id) => new()
    {
        DroneId = id,
        Availability = "unavailable",
        Reason = "Entity unavailable"
    };

    private static List<CellObservation> Cells(
        DronePosition position,
        Func<int, int, int, bool> solid)
    {
        var x = (int)Math.Floor(position.X);
        var y = (int)Math.Floor(position.Y);
        var z = (int)Math.Floor(position.Z);
        var cells = new List<CellObservation>();
        for (var cx = Math.Max(-64, x - 3); cx <= Math.Min(63, x + 3); cx++)
        for (var cy = Math.Max(0, y - 3); cy <= Math.Min(63, y + 3); cy++)
        for (var cz = Math.Max(-64, z - 3); cz <= Math.Min(63, z + 3); cz++)
            cells.Add(new() { X = cx, Y = cy, Z = cz, Solid = solid(cx, cy, cz) });
        return cells;
    }

    private static bool CellIsSolid(int x, int y, int z) =>
        y <= 10 || (x == 8 && y == 12 && z == 0);
}
