namespace DroneSwarm.Web.Tests;

public sealed class SurfaceExplorationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplorationStaysWithinThreeBlocksOfGroundOrCeiling(bool ceiling)
    {
        var simulation = new Simulation((_, y, _) => ceiling ? y >= 14 : y <= 10);
        var visited = new HashSet<(int X, int Z)>();
        for (var step = 0; step < 70; step++)
        {
            var command = Assert.Single(simulation.Step().Commands);
            Assert.InRange(command.Destination.Y, 11, 13);
            AssertNearKnownSurface(simulation, command);
            visited.Add((command.Destination.X, command.Destination.Z));
        }
        Assert.True(visited.Count > 10, "Drones must keep exploring horizontally, not just hover.");
    }

    [Fact]
    public void ExplorationFollowsRisingGroundWithoutCrossingAnUnsupportedGap()
    {
        var simulation = new Simulation((x, y, _) => x <= 2 && y <= 10 + Math.Min(4, Math.Max(0, -x / 4)));
        var climbed = false;
        for (var step = 0; step < 65; step++)
        {
            var command = Assert.Single(simulation.Step().Commands);
            AssertNearKnownSurface(simulation, command);
            climbed |= command.Destination.Y > 12;
        }
        Assert.True(climbed, "Surface following must allow climbing actual terrain.");
    }

    [Fact]
    public void EmptyAirDoesNotCreateAnUpwardExplorationRoute()
    {
        var simulation = new Simulation((_, _, _) => false);
        Assert.Empty(simulation.Step().Commands);
        Assert.Equal("stuck", Assert.Single(simulation.Service.GetSnapshot().Drones).Behavior);
    }

    [Fact]
    public void ADroneInOpenAirApproachesAPreviouslyObservedSurface()
    {
        var simulation = new Simulation((_, y, _) => y <= 10);
        simulation.Position = new() { X = 0.5, Y = 15.5, Z = 0.5 };
        var command = Assert.Single(simulation.Step().Commands);
        Assert.Equal(14, command.Destination.Y);
        Assert.Equal(0, command.Destination.X);
        Assert.Equal(0, command.Destination.Z);
    }

    [Fact]
    public void TargetReportingCanReturnThroughOpenAirWithoutASurface()
    {
        var simulation = new Simulation((_, _, _) => false, new() { X = 8, Y = 12, Z = 0 });
        simulation.Position = new() { X = 5.5, Y = 12.5, Z = 0.5 };
        var command = Assert.Single(simulation.Step().Commands);
        Assert.Equal(new BlockPoint { X = 4, Y = 12, Z = 0 }, command.Destination);
        Assert.NotNull(simulation.Service.GetDroneDebug("SCOUT").KnownTarget);
        Assert.False(simulation.Service.GetSnapshot().TargetReported);
        simulation.Step();
        simulation.Step();
        Assert.True(simulation.Service.GetSnapshot().TargetReported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScoutsReturnToUploadTerrainAndThenResumeExploration(bool solidHive)
    {
        var simulation = new Simulation(
            (x, y, z) => y <= 10 || (solidHive && x == 0 && y == 12 && z == 0),
            start: new() { X = 0.5, Y = solidHive ? 13.5 : 12.5, Z = 0.5 });
        var sawUploadReturn = false;
        var resumed = false;
        var terrainBeforeReturn = 0;
        for (var step = 0; step < 100; step++)
        {
            simulation.Step();
            var snapshot = simulation.Service.GetSnapshot();
            var drone = Assert.Single(snapshot.Drones);
            if (drone.Behavior == "returningToUpload" && !sawUploadReturn)
            {
                sawUploadReturn = true;
                terrainBeforeReturn = snapshot.Terrain.Count;
            }
            if (sawUploadReturn && drone.ConnectedToTower && drone.Behavior != "returningToUpload")
            {
                Assert.True(snapshot.Terrain.Count > terrainBeforeReturn, "Remote discoveries must reach the tower.");
                resumed = true;
                break;
            }
        }
        Assert.True(sawUploadReturn, "An isolated scout must carry discoveries home periodically.");
        Assert.True(resumed, "A successful upload must allow exploration to resume.");
    }

    [Fact]
    public void TargetReportingAndRecoveryReturnToAFreeNeighbourOfASolidHive()
    {
        var simulation = new Simulation(
            (x, y, z) => y <= 10 || (y == 12 && z == 0 && (x == 0 || x == 8)),
            new() { X = 8, Y = 12, Z = 0 },
            new() { X = 0.5, Y = 13.5, Z = 0.5 });
        simulation.Position = new() { X = 5.5, Y = 12.5, Z = 0.5 };

        var command = Assert.Single(simulation.Step().Commands);
        Assert.Equal(new BlockPoint { X = 4, Y = 12, Z = 0 }, command.Destination);
        Assert.NotNull(simulation.Service.GetDroneDebug("SCOUT").KnownTarget);
        Assert.False(simulation.Service.GetSnapshot().TargetReported);
        for (var step = 0; step < 12 && simulation.Service.GetSnapshot().Phase != "complete"; step++)
            simulation.Step();

        Assert.Equal("complete", simulation.Service.GetSnapshot().Phase);
        Assert.True(simulation.Service.GetSnapshot().TargetReported);
        Assert.Equal(1, simulation.Service.GetSnapshot().RecoveredCount);
    }

    private static void AssertNearKnownSurface(Simulation simulation, MovementCommand command)
    {
        var destination = command.Destination;
        var cells = simulation.Service.GetDroneDebug("SCOUT").Cells;
        Assert.Contains(cells, cell => !cell.Solid &&
            cell.X == destination.X && cell.Y == destination.Y && cell.Z == destination.Z);
        Assert.Contains(cells, cell => cell.Solid &&
            Math.Pow(cell.X - destination.X, 2) +
            Math.Pow(cell.Y - destination.Y, 2) +
            Math.Pow(cell.Z - destination.Z, 2) <= 9);
    }

    private sealed class Simulation
    {
        private readonly Func<int, int, int, bool> _solid;
        private readonly string _missionId = Guid.NewGuid().ToString();
        private MovementCommand? _pending;
        private long _sequence;

        public MissionService Service { get; } = new();
        public DronePosition Position { get; set; } = new() { X = 0.5, Y = 12.5, Z = 0.5 };

        public Simulation(Func<int, int, int, bool> solid, BlockPoint? target = null, DronePosition? start = null)
        {
            _solid = solid;
            if (start is not null) Position = start;
            Service.Initialize(new()
            {
                ProtocolVersion = 1,
                MissionId = _missionId,
                WorldId = "surface-tests",
                BoundsMin = new() { X = -64, Y = 0, Z = -64 },
                Tower = new() { X = 0, Y = 12, Z = 0 },
                Target = target ?? new() { X = 60, Y = 12, Z = 60 },
                InitialFrame = new() { Sequence = 0, GameTick = 0, Drones = [Telemetry(null)] }
            }, "initial");
        }

        public AcceptedFrameResponse Step()
        {
            CommandResult? result = null;
            if (_pending is not null)
            {
                var destination = _pending.Destination;
                Assert.False(_solid(destination.X, destination.Y, destination.Z));
                Position = new() { X = destination.X + 0.5, Y = destination.Y + 0.5, Z = destination.Z + 0.5 };
                result = new() { CommandId = _pending.CommandId, Status = "completed" };
            }
            _sequence++;
            var response = Service.AcceptFrame(_missionId, new()
            {
                Sequence = _sequence,
                GameTick = _sequence * 10,
                Drones = [Telemetry(result)]
            }, $"frame-{_sequence}");
            _pending = response.Commands.SingleOrDefault();
            return response;
        }

        private DroneTelemetry Telemetry(CommandResult? result)
        {
            var x = (int)Math.Floor(Position.X);
            var y = (int)Math.Floor(Position.Y);
            var z = (int)Math.Floor(Position.Z);
            var cells = new List<CellObservation>();
            for (var cx = Math.Max(-64, x - 3); cx <= Math.Min(63, x + 3); cx++)
            for (var cy = Math.Max(0, y - 3); cy <= Math.Min(63, y + 3); cy++)
            for (var cz = Math.Max(-64, z - 3); cz <= Math.Min(63, z + 3); cz++)
                cells.Add(new() { X = cx, Y = cy, Z = cz, Solid = _solid(cx, cy, cz) });
            return new()
            {
                DroneId = "SCOUT",
                Availability = "available",
                Position = Position,
                Cells = cells,
                CommandResult = result
            };
        }
    }
}
