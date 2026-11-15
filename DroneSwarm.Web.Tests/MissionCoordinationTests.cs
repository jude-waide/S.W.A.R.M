using System.Diagnostics;
using Xunit.Abstractions;

namespace DroneSwarm.Web.Tests;

public sealed class MissionCoordinationTests(ITestOutputHelper output)
{
    [Fact]
    public void ContinuousFrontierArrivalWaitsForSensingInsteadOfCirclingUnobservedViewpoints()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"], continuousFlight: true);
        var command = Assert.Single(Accept(service, id, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid)).Commands);
        var position = new DronePosition
        {
            X = command.Destination.X + 0.5,
            Y = command.Destination.Y + 0.5,
            Z = command.Destination.Z + 0.5
        };
        var knowledge = service.GetDroneDebug("A").Cells;
        var arrived = new DroneTelemetry
        {
            DroneId = "A", Availability = "available", Position = position,
            CommandResult = new() { CommandId = command.CommandId, Status = "completed" }
        };
        Assert.Empty(service.AcceptFrame(id, new()
        {
            Sequence = 2, GameTick = 12, Drones = [arrived]
        }, "frontier-arrived", motionOnly: true).Commands);
        arrived.CommandResult = null;
        Assert.Empty(service.AcceptFrame(id, new()
        {
            Sequence = 3, GameTick = 14, Drones = [arrived]
        }, "frontier-still-waiting", motionOnly: true).Commands);
        Assert.Equal(knowledge, service.GetDroneDebug("A").Cells);
        Assert.NotEmpty(Accept(service, id, 4, 20, Drone("A", position, CellIsSolid)).Commands);
    }

    [Fact]
    public void ContinuousFrontierCanBufferDepartureWhenSensedBeforeArrivalAcknowledgement()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"], continuousFlight: true);
        var command = Assert.Single(Accept(service, id, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid)).Commands);
        var response = Accept(service, id, 2, 20, Drone("A", new()
        {
            X = command.Destination.X + 0.5,
            Y = command.Destination.Y + 0.5,
            Z = command.Destination.Z + 0.5
        }, CellIsSolid));
        Assert.Equal(command.CommandId, response.Commands[0].CommandId);
        Assert.Equal(command.CommandId, response.Commands[1].AfterCommandId);
    }

    [Fact]
    public void ContinuousReturnReservesAndChainsOneFutureMove()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 6, Y = 12, Z = 0 }, ["A", "B"], continuousFlight: true);
        var response = Accept(service, id, 1, 10,
            Drone("A", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 1.5 }, CellIsSolid));
        Assert.Equal(4, response.Commands.Count);
        Assert.Equal(4, response.Commands.Select(command => command.Destination).Distinct().Count());
        foreach (var group in response.Commands.GroupBy(command => command.DroneId))
        {
            var moves = group.ToArray();
            Assert.Null(moves[0].AfterCommandId);
            Assert.Equal(moves[0].CommandId, moves[1].AfterCommandId);
            Assert.Equal(1, Math.Abs(moves[0].Destination.X - moves[1].Destination.X) +
                Math.Abs(moves[0].Destination.Y - moves[1].Destination.Y) +
                Math.Abs(moves[0].Destination.Z - moves[1].Destination.Z));
        }
    }

    [Fact]
    public void MotionFramesPromoteBufferedMovesWithoutSensingRelayingOrRecordingTerrain()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 6, Y = 12, Z = 0 }, ["A"], continuousFlight: true);
        var response = Accept(service, id, 1, 10,
            Drone("A", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        var current = response.Commands[0];
        var queued = response.Commands[1];
        var knowledge = service.GetDroneDebug("A");
        var replay = service.GetReplayIndex();
        var telemetry = new DroneTelemetry
        {
            DroneId = "A", Availability = "available",
            Position = new() { X = current.Destination.X + 0.5, Y = current.Destination.Y + 0.5, Z = current.Destination.Z + 0.5 },
            CommandResult = new() { CommandId = current.CommandId, Status = "completed" }
        };
        var frame = new ObservationFrame { Sequence = 2, GameTick = 12, Drones = [telemetry] };
        var promoted = service.AcceptFrame(id, frame, "motion", motionOnly: true);
        Assert.Equal(queued.CommandId, promoted.Commands[0].CommandId);
        Assert.Equal(queued.CommandId, promoted.Commands[1].AfterCommandId);
        Assert.Equal(current.CommandId, Assert.Single(promoted.AcknowledgedResults).CommandId);
        Assert.Same(promoted, service.AcceptFrame(id, frame, "motion", motionOnly: true));
        Assert.Equal(knowledge.Cells, service.GetDroneDebug("A").Cells);
        Assert.Equal(knowledge.PeerPositions, service.GetDroneDebug("A").PeerPositions);
        Assert.Equal(replay.GameTicks, service.GetReplayIndex().GameTicks);
        var live = Assert.Single(service.GetMotionSnapshot().Drones).Position!;
        Assert.Equal((telemetry.Position.X, telemetry.Position.Y, telemetry.Position.Z), (live.X, live.Y, live.Z));
        Assert.False(Assert.Single(service.GetSnapshot().Drones).PositionCurrent);
        Assert.Equal(0.5, Assert.Single(service.GetSnapshot().Drones).Position!.X);
        Assert.Equal("changed_retry", Assert.Throws<ProtocolException>(
            () => service.AcceptFrame(id, frame, "motion")).Code);

        telemetry.CommandResult = new() { CommandId = queued.CommandId, Status = "blocked", Reason = "New obstacle" };
        var blocked = service.AcceptFrame(id, new() { Sequence = 3, GameTick = 14, Drones = [telemetry] },
            "blocked-motion", motionOnly: true);
        Assert.DoesNotContain(blocked.Commands, command => command.CommandId == promoted.Commands[1].CommandId);
        Assert.All(blocked.Commands.Where(command => command.AfterCommandId is not null),
            command => Assert.Contains(blocked.Commands, predecessor => predecessor.CommandId == command.AfterCommandId));
    }

    [Fact]
    public void ContinuousReturnStopsAtHiveUntilTheNextRealKnowledgeCycleConfirmsRecovery()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 6, Y = 12, Z = 0 }, ["A"],
            new() { X = 3.5, Y = 12.5, Z = 0.5 }, continuousFlight: true);
        var command = Assert.Single(Accept(service, id, 1, 10,
            Drone("A", new() { X = 4.5, Y = 12.5, Z = 0.5 }, CellIsSolid)).Commands);
        Assert.Equal(3, command.Destination.X);
        var position = new DronePosition { X = 3.5, Y = 12.5, Z = 0.5 };
        var response = service.AcceptFrame(id, new()
        {
            Sequence = 2, GameTick = 12,
            Drones = [new()
            {
                DroneId = "A", Availability = "available", Position = position,
                CommandResult = new() { CommandId = command.CommandId, Status = "completed" }
            }]
        }, "arrived", motionOnly: true);
        Assert.Empty(response.Commands);
        Assert.False(service.GetSnapshot().TargetReported);
        Accept(service, id, 3, 20, Drone("A", position, CellIsSolid));
        Assert.Equal("complete", service.GetSnapshot().Phase);
    }

    [Fact]
    public void MotionValidationIsAtomicAndRequiresOptIn()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"]);
        var frame = new ObservationFrame { Sequence = 1, GameTick = 10,
            Drones = [Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid)] };
        Assert.Equal("motion_not_enabled", Assert.Throws<ProtocolException>(
            () => service.AcceptFrame(id, frame, "motion", motionOnly: true)).Code);
        service.Reset(id);
        id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"], continuousFlight: true);
        Assert.Equal("unexpected_cells", Assert.Throws<ProtocolException>(
            () => service.AcceptFrame(id, frame, "motion", motionOnly: true)).Code);
        Assert.Equal(0, service.GetSnapshot().GameTick);
        Assert.Single(service.GetReplayIndex().GameTicks);
        frame.Drones[0].Cells.Clear();
        var response = service.AcceptFrame(id, frame, "valid-motion", motionOnly: true);
        Assert.Empty(response.Commands);
        Assert.Equal(1, response.Sequence);
    }

    [Fact]
    public void SpawnedScoutsCanStartBehindAnObstacleWithoutInventingALiveHiveLink()
    {
        var service = new MissionService();
        Initialize(service, Guid.NewGuid().ToString(), new() { X = 60, Y = 12, Z = 60 }, ["A"],
            new() { X = 2.5, Y = 12.5, Z = 0.5 },
            (x, y, z) => CellIsSolid(x, y, z) || (x == 1 && y == 12 && z == 0));

        var scout = Assert.Single(service.GetSnapshot().Drones);
        Assert.False(scout.DirectlyConnectedToTower);
        Assert.False(scout.ConnectedToTower);
        Assert.Equal(2.5, scout.Position!.X);
    }

    [Fact]
    public void ScoutSightingsReachTheHiveAndAnotherScoutOnlyThroughKnowledgeExchange()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A", "B", "C"]);
        Accept(service, id, 1, 10,
            Drone("A", new() { X = 3.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 6.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("C", new() { X = -20.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Equal(0.5, service.GetSnapshot().Drones.Single(drone => drone.Id == "B").Position!.X);
        Assert.Equal(6.5, service.GetDroneDebug("A").PeerPositions.Single(peer => peer.DroneId == "B").Position.X);

        Accept(service, id, 2, 20,
            Drone("A", new() { X = 3.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 20.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("C", new() { X = -20.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        var hiveSighting = service.GetSnapshot().Drones.Single(drone => drone.Id == "B");
        Assert.Equal(6.5, hiveSighting.Position!.X);
        Assert.Equal(10, hiveSighting.LastSeenTick);
        Assert.False(hiveSighting.PositionCurrent);
        Assert.DoesNotContain(service.GetDroneDebug("C").PeerPositions, peer => peer.DroneId == "B" && peer.SourceTick == 10);

        Accept(service, id, 3, 30,
            Drone("A", new() { X = 3.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 21.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("C", new() { X = -2.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        var relayed = service.GetDroneDebug("C").PeerPositions.Single(peer => peer.DroneId == "B");
        Assert.Equal(6.5, relayed.Position.X);
        Assert.Equal(10, relayed.SourceTick);
    }

    [Fact]
    public void DiscoveryAndHiveReportingAreSeparateAndCompletionWaitsForOutstandingMovement()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 20, Y = 12, Z = 0 }, ["A"]);
        var exploring = Accept(service, id, 1, 10,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        var command = Assert.Single(exploring.Commands);
        Accept(service, id, 2, 20,
            Drone("A", new() { X = 18.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.True(service.GetSnapshot().TargetFound);
        Assert.False(service.GetSnapshot().TargetReported);
        Accept(service, id, 3, 30,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.True(service.GetSnapshot().TargetReported);
        Assert.NotEqual("complete", service.GetSnapshot().Phase);
        Assert.Equal(0, service.GetSnapshot().RecoveredCount);

        var stopped = Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid);
        stopped.CommandResult = new() { CommandId = command.CommandId, Status = "completed" };
        var response = Accept(service, id, 4, 40, stopped);
        Assert.Equal("complete", service.GetSnapshot().Phase);
        Assert.Empty(response.Commands);
        var frames = service.GetReplayIndex().GameTicks.Count;
        Accept(service, id, 5, 50,
            Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Equal(frames, service.GetReplayIndex().GameTicks.Count);
    }

    [Fact]
    public void ReplayRecordsEveryAcceptedFramePreservesOldKnowledgeAndSurvivesResetUntilNextMission()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"]);
        var frame = new ObservationFrame
        {
            Sequence = 1, GameTick = 10,
            Drones = [Drone("A", new() { X = 12.5, Y = 12.5, Z = 0.5 },
                (x, y, z) => CellIsSolid(x, y, z) || (x == 14 && y == 12 && z == 0))]
        };
        service.AcceptFrame(id, frame, "same-frame");
        service.AcceptFrame(id, frame, "same-frame");
        Accept(service, id, 2, 20,
            Drone("A", new() { X = 12.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Equal([0L, 10L, 20L], service.GetReplayIndex().GameTicks);
        var first = service.GetReplayFrame(id, 1);
        Assert.Contains(first.LiveTerrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
        Assert.DoesNotContain(first.Terrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
        Assert.DoesNotContain(service.GetReplayFrame(id, 2).LiveTerrain, cell => cell.X == 14 && cell.Y == 12 && cell.Z == 0);
        Assert.Equal(0.5, service.GetReplayFrame(id, 0).Drones[0].LivePosition!.X);
        Assert.Equal(400, Assert.Throws<ProtocolException>(() => service.GetReplayFrame(id, -1)).StatusCode);
        service.Reset(id);
        Assert.Equal(id, service.GetReplayIndex().MissionId);
        Assert.Equal(10, service.GetReplayFrame(id, 1).GameTick);
        var next = Guid.NewGuid().ToString();
        Initialize(service, next, new() { X = 60, Y = 12, Z = 60 }, ["B"]);
        Assert.Equal(next, service.GetReplayIndex().MissionId);
        Assert.Single(service.GetReplayIndex().GameTicks);
        Assert.Equal(404, Assert.Throws<ProtocolException>(() => service.GetReplayFrame(id, 0)).StatusCode);
    }

    [Fact]
    public void ReplayRecordingCanBePausedAndResumedWithoutLosingRecordedFrames()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"]);
        Assert.True(service.GetReplayIndex().RecordingEnabled);

        service.SetReplayRecordingEnabled(false);
        Accept(service, id, 1, 10, Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Equal([0L], service.GetReplayIndex().GameTicks);
        Assert.False(service.GetReplayIndex().RecordingEnabled);

        service.SetReplayRecordingEnabled(true);
        Accept(service, id, 2, 20, Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Equal([0L, 20L], service.GetReplayIndex().GameTicks);
        Assert.True(service.GetReplayIndex().RecordingEnabled);
    }

    [Fact]
    public void EnablingReplayRecordingMidMissionStartsWithTheCurrentSnapshot()
    {
        var service = new MissionService();
        service.SetReplayRecordingEnabled(false);
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A"]);
        Accept(service, id, 1, 10, Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Empty(service.GetReplayIndex().GameTicks);

        var replay = service.SetReplayRecordingEnabled(true);
        Assert.Equal(id, replay.MissionId);
        Assert.Equal([10L], replay.GameTicks);
        Assert.True(replay.RecordingEnabled);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatureConnectedSwarmLeavesTimeForMotionFramesBetweenObservationCycles(bool variedObservationAges)
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, 20).Select(index => index.ToString()).ToArray();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ids, continuousFlight: true);
        long sequence = 0;
        var viewpoints = (from z in Enumerable.Range(0, 13)
                          from x in Enumerable.Range(0, 19)
                          select new DronePosition { X = -54 + x * 6 + 0.5, Y = 12.5, Z = -36 + z * 6 + 0.5 }).ToArray();
        for (var index = 0; index < viewpoints.Length; index++)
            Accept(service, id, ++sequence, variedObservationAges ? index * 9 / viewpoints.Length : 0,
                ids.Select((droneId, owner) => Drone(droneId,
                    viewpoints[(index + (variedObservationAges ? owner * 11 : 0)) % viewpoints.Length], CellIsSolid)).ToArray());
        var telemetry = ids.Select((droneId, index) => Drone(droneId, new()
        {
            X = index % 5 + 0.5, Y = 12.5, Z = index / 5 + 0.5
        }, CellIsSolid)).ToArray();
        var timer = Stopwatch.StartNew();
        Accept(service, id, ++sequence, 10, telemetry);
        var snapshot = service.GetSnapshot();
        timer.Stop();
        output.WriteLine($"Mature connected continuous-flight observation and snapshot: {timer.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.All(snapshot.Drones, drone => Assert.True(drone.KnownCellCount > 60_000));
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500),
            $"A mature connected swarm took {timer.Elapsed.TotalMilliseconds:F0} ms, starving 2-tick motion frames.");
        for (var tick = 20; tick <= 40; tick += 10)
            Accept(service, id, ++sequence, tick, telemetry);
        timer.Restart();
        Accept(service, id, ++sequence, 50, telemetry);
        service.GetSnapshot();
        timer.Stop();
        output.WriteLine($"Steady mature observation and snapshot: {timer.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Steady mature-map updates took {timer.Elapsed.TotalMilliseconds:F0} ms, starving motion frames.");
    }

    [Fact]
    public void MatureReturningSwarmKeepsLongDistanceRoutesWithinTheMotionBudget()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, 20).Select(index => index.ToString()).ToArray();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ids, continuousFlight: true);
        long sequence = 0;
        for (var z = -60; z <= 60; z += 6)
        for (var x = -60; x <= 60; x += 6)
            Accept(service, id, ++sequence, 0,
                ids.Select(droneId => Drone(droneId, new() { X = x + 0.5, Y = 12.5, Z = z + 0.5 }, CellIsSolid)).ToArray());
        var telemetry = ids.Select((droneId, index) => Drone(droneId, new()
        {
            X = 57.5 + index % 5, Y = 12.5, Z = 57.5 + index / 5
        }, CellIsSolid)).ToArray();
        var timer = Stopwatch.StartNew();
        var response = Accept(service, id, ++sequence, 10, telemetry);
        var snapshot = service.GetSnapshot();
        timer.Stop();
        Assert.True(snapshot.TargetFound);
        Assert.NotEmpty(response.Commands);
        Assert.All(snapshot.Drones, drone => Assert.True(drone.KnownCellCount > 100_000));
        output.WriteLine($"Mature long-distance return planning: {timer.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Long-distance returns took {timer.Elapsed.TotalMilliseconds:F0} ms, starving motion frames.");
    }

    [Fact]
    public void PeerLinksShareHistoricalTerrainOnContactAndLaterAirChangesAsDeltas()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ["A", "B"]);
        Accept(service, id, 1, 10,
            Drone("A", new() { X = 33.5, Y = 12.5, Z = 0.5 },
                (x, y, z) => CellIsSolid(x, y, z) || (x == 36 && y == 12 && z == 0)),
            Drone("B", new() { X = -20.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Accept(service, id, 2, 20,
            Drone("A", new() { X = 20.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = -20.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.DoesNotContain(service.GetDroneDebug("B").Cells, cell => cell.X == 36 && cell.Solid);
        Accept(service, id, 3, 30,
            Drone("A", new() { X = 20.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 19.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Contains(service.GetDroneDebug("B").Cells,
            cell => cell.X == 36 && cell.Y == 12 && cell.Z == 0 && cell.Solid && cell.SourceTick == 10);
        Assert.DoesNotContain(service.GetSnapshot().Terrain, cell => cell.X == 36);
        Accept(service, id, 4, 40,
            Drone("A", new() { X = 20.5, Y = 12.5, Z = 0.5 },
                (x, y, z) => CellIsSolid(x, y, z) || (x == 23 && y == 12 && z == 0)),
            Drone("B", new() { X = 19.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Contains(service.GetDroneDebug("B").Cells,
            cell => cell.X == 23 && cell.Y == 12 && cell.Z == 0 && cell.Solid && cell.SourceTick == 40);
        Accept(service, id, 5, 50,
            Drone("A", new() { X = 20.5, Y = 12.5, Z = 0.5 }, CellIsSolid),
            Drone("B", new() { X = 19.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.Contains(service.GetDroneDebug("B").Cells,
            cell => cell.X == 23 && cell.Y == 12 && cell.Z == 0 && !cell.Solid && cell.SourceTick == 50);
    }

    [Fact]
    public void BatchedKnowledgeRelaySupportsMoreThanSixtyFourSourceOwners()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, 70).Select(index => index.ToString()).ToArray();
        Initialize(service, id, new() { X = 60, Y = 12, Z = 60 }, ids);
        Accept(service, id, 1, 10, ids.Select(droneId => Drone(droneId,
            new() { X = droneId == "69" ? 2.5 : 0.5, Y = 12.5, Z = 0.5 },
            (x, y, z) => CellIsSolid(x, y, z) || (x == 5 && y == 12 && z == 0))).ToArray());
        Assert.Contains(service.GetDroneDebug("0").Cells, cell => cell.X == 5 && cell.Y == 12 && cell.Z == 0 && cell.Solid);
    }

    [Fact]
    public void TwentyScoutsWithMatureMapsPlanAndPublishWithinOneSecond()
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, 20).Select(index => $"SCOUT-{index:00}").ToArray();
        Initialize(service, missionId, new() { X = 60, Y = 12, Z = 60 }, ids);
        long sequence = 0;
        // Seed accumulated observations without including warmup decisions in the timing.
        for (var z = -24; z <= 24; z += 6)
        for (var x = -48; x <= 48; x += 6)
            Accept(service, missionId, ++sequence, 0,
                ids.Select(id => Drone(id, new() { X = x + 0.5, Y = 12.5, Z = z + 0.5 }, CellIsSolid)).ToArray());

        var telemetry = ids.Select((id, index) => Drone(id, new()
        {
            X = -42.5 + index % 7 * 12,
            Y = 12.5,
            Z = -18.5 + index / 7 * 12
        }, CellIsSolid)).ToArray();
        var timer = Stopwatch.StartNew();
        var response = Accept(service, missionId, ++sequence, 10, telemetry);
        var snapshot = service.GetSnapshot();
        timer.Stop();
        output.WriteLine($"Mature 20-scout decision and snapshot: {timer.Elapsed.TotalMilliseconds:F0} ms.");

        Assert.Equal(20, response.Commands.Count);
        Assert.All(snapshot.Drones, drone => Assert.True(drone.KnownCellCount > 30_000));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1),
            $"A mature 20-scout decision and snapshot took {timer.Elapsed.TotalMilliseconds:F0} ms; it must stay below half the plugin's 2-second timeout.");
    }

    [Theory]
    [InlineData(12, 1, 12, 0, false)]
    [InlineData(14, 1, 12, 0, false)]
    [InlineData(12, 1, 12, 1, false)]
    [InlineData(12, 2, 12, 0, true)]
    [InlineData(12, 2, 12, 2, true)]
    public void TargetVisibilityPreservesEdgeCornerAndEndpointOcclusion(
        int targetY, int solidX, int solidY, int solidZ, bool visible)
    {
        var service = new MissionService();
        var missionId = Guid.NewGuid().ToString();
        Initialize(service, missionId, new() { X = 2, Y = targetY, Z = 2 }, ["A"]);
        Accept(service, missionId, 1, 10, Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 },
            (x, y, z) => CellIsSolid(x, y, z) || (x == solidX && y == solidY && z == solidZ)));

        Assert.Equal(visible, service.GetDroneDebug("A").KnownTarget is not null);
        Assert.Equal(visible, service.GetSnapshot().TargetReported);
    }

    [Fact]
    public void UpdatedAirRemovesAFormerTargetOccluderFromScoutSensing()
    {
        var service = new MissionService();
        var id = Guid.NewGuid().ToString();
        Initialize(service, id, new() { X = 2, Y = 12, Z = 2 }, ["A"]);
        Accept(service, id, 1, 10, Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 },
            (x, y, z) => CellIsSolid(x, y, z) || (x == 1 && y == 12 && z == 0)));
        Assert.Null(service.GetDroneDebug("A").KnownTarget);
        Accept(service, id, 2, 20, Drone("A", new() { X = 0.5, Y = 12.5, Z = 0.5 }, CellIsSolid));
        Assert.NotNull(service.GetDroneDebug("A").KnownTarget);
    }

    [Fact]
    public void NewPeerContactDefersOnlyTheLaterUploadAndDoesNotKeepResettingWhileLinked()
    {
        var simulation = new EncounterSimulation("A", "B");
        simulation.Advance("B", 6);
        simulation.Advance("A", 2);
        simulation.Meet("B", "A");

        Assert.Equal(0, simulation.Debug("A").FrontierMovesSinceUpload);
        Assert.Equal(6, simulation.Debug("B").FrontierMovesSinceUpload);
        simulation.Step("A");
        Assert.Contains("B", simulation.Debug("A").ConnectedPeers);
        Assert.Equal(1, simulation.Debug("A").FrontierMovesSinceUpload);

        var near = simulation.Positions["B"];
        simulation.Positions["B"] = new() { X = near.X, Y = near.Y, Z = near.Z + 20 };
        simulation.Step();
        Assert.DoesNotContain("B", simulation.Debug("A").ConnectedPeers);
        simulation.Meet("B", "A");
        Assert.Equal(0, simulation.Debug("A").FrontierMovesSinceUpload);
        Assert.Equal(6, simulation.Debug("B").FrontierMovesSinceUpload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimultaneousEqualCounterEncountersPreserveOneUploaderRegardlessOfRosterOrder(bool reverseRoster)
    {
        var simulation = reverseRoster
            ? new EncounterSimulation("C", "B", "A")
            : new EncounterSimulation("A", "B", "C");
        foreach (var id in new[] { "A", "B", "C" }) simulation.Advance(id, 3);
        var anchor = simulation.Positions["A"];
        simulation.Positions["B"] = new() { X = anchor.X, Y = anchor.Y, Z = anchor.Z + 1 };
        simulation.Positions["C"] = new() { X = anchor.X, Y = anchor.Y, Z = anchor.Z + 2 };
        simulation.Step();

        Assert.Equal(3, simulation.Debug("A").FrontierMovesSinceUpload);
        Assert.Equal(0, simulation.Debug("B").FrontierMovesSinceUpload);
        Assert.Equal(0, simulation.Debug("C").FrontierMovesSinceUpload);
    }

    [Fact]
    public void AnEncounterKeepsAnAlreadyReturningUploaderOnSchedule()
    {
        var simulation = new EncounterSimulation("A", "B");
        simulation.Advance("B", 24);
        simulation.Positions["B"] = new() { X = 4.5, Y = 12.5, Z = 0.5 };
        simulation.Step();
        simulation.Advance("A", 6);
        Assert.Equal("returningToUpload", simulation.Debug("B").Behavior);
        simulation.Meet("B", "A");

        Assert.Equal(0, simulation.Debug("A").FrontierMovesSinceUpload);
        Assert.Equal(24, simulation.Debug("B").FrontierMovesSinceUpload);
        Assert.Equal("returningToUpload", simulation.Debug("B").Behavior);
    }

    [Fact]
    public void TargetKnowledgeStillTakesPriorityOverDeferringAnUpload()
    {
        var simulation = new EncounterSimulation("A", "B");
        simulation.Advance("B", 6);
        simulation.Advance("A", 2);
        simulation.Positions["B"] = new() { X = 58.5, Y = 12.5, Z = 58.5 };
        simulation.Step();
        Assert.NotNull(simulation.Debug("B").KnownTarget);
        simulation.Meet("B", "A");

        Assert.NotNull(simulation.Debug("A").KnownTarget);
        Assert.Equal(2, simulation.Debug("A").FrontierMovesSinceUpload);
        Assert.Equal(6, simulation.Debug("B").FrontierMovesSinceUpload);
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

    private sealed class EncounterSimulation
    {
        private readonly MissionService _service = new();
        private readonly string _missionId = Guid.NewGuid().ToString();
        private Dictionary<string, MovementCommand> _pending = [];
        private long _sequence;
        public Dictionary<string, DronePosition> Positions { get; } = [];

        public EncounterSimulation(params string[] ids)
        {
            Initialize(_service, _missionId, new() { X = 60, Y = 12, Z = 60 }, ids);
            for (var index = 0; index < ids.Length; index++)
                Positions[ids[index]] = new() { X = 20.5, Y = 12.5, Z = -29.5 + index * 60.0 / (ids.Length - 1) };
            Step();
        }

        public DroneDebug Debug(string id) => _service.GetDroneDebug(id);

        public void Advance(string id, int moves)
        {
            for (var step = 0; Debug(id).FrontierMovesSinceUpload < moves && step < 60; step++) Step(id);
            Assert.Equal(moves, Debug(id).FrontierMovesSinceUpload);
        }

        public void Meet(string movingId, string anchorId)
        {
            var anchor = Positions[anchorId];
            Positions[movingId] = new() { X = anchor.X, Y = anchor.Y, Z = anchor.Z + 1 };
            Step();
            Assert.Contains(movingId, Debug(anchorId).ConnectedPeers);
        }

        public void Step(params string[] complete)
        {
            var telemetry = new List<DroneTelemetry>();
            foreach (var (id, original) in Positions.ToArray())
            {
                var position = original;
                CommandResult? result = null;
                if (complete.Contains(id) && _pending.TryGetValue(id, out var command))
                {
                    var destination = command.Destination;
                    position = new() { X = destination.X + 0.5, Y = destination.Y + 0.5, Z = destination.Z + 0.5 };
                    Positions[id] = position;
                    result = new() { CommandId = command.CommandId, Status = "completed" };
                }
                var drone = Drone(id, position, CellIsSolid);
                drone.CommandResult = result;
                telemetry.Add(drone);
            }
            _sequence++;
            var response = Accept(_service, _missionId, _sequence, _sequence * 10, [.. telemetry]);
            _pending = response.Commands.ToDictionary(command => command.DroneId);
        }
    }

    private static void Initialize(MissionService service, string missionId, BlockPoint target, string[] droneIds,
        DronePosition? initialPosition = null, Func<int, int, int, bool>? solid = null, bool continuousFlight = false)
    {
        service.Initialize(new()
        {
            ProtocolVersion = 1,
            ContinuousFlight = continuousFlight,
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
                    .Select(id => Drone(id, initialPosition ?? new() { X = 0.5, Y = 12.5, Z = 0.5 }, solid ?? CellIsSolid))
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
