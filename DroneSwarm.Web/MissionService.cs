using System.Diagnostics;

namespace DroneSwarm.Web;

public sealed class MissionService
{
    private const int Width = 128;
    private const int Height = 64;
    private const int Depth = 128;
    private const int SurfaceDistance = 3;
    private const int FrontierMovesBeforeUpload = 24;
    private readonly object _gate = new();
    private readonly HashSet<string> _retiredMissionIds = new(StringComparer.Ordinal);
    private Mission? _mission;
    private string? _lastResetMissionId;

    public PluginStatus GetPluginStatus()
    {
        lock (_gate)
            return _mission is null
                ? new(1, null, "awaitingMission")
                : new(1, _mission.Id, _mission.Phase);
    }

    public AcceptedFrameResponse Initialize(InitializeMissionRequest request, string requestHash)
    {
        lock (_gate)
        {
            ValidateInitialization(request);
            if (_mission is not null)
            {
                if (_mission.Id == request.MissionId && _mission.InitialHash == requestHash)
                    return _mission.LastResponse;
                throw new ProtocolException(409, "active_mission", "An active mission must be reset before another mission can start.");
            }

            if (_retiredMissionIds.Contains(request.MissionId))
                throw new ProtocolException(409, "retired_mission_id", "Mission IDs cannot be reused during this server process.");

            var mission = new Mission(request, requestHash);
            var initialStates = request.InitialFrame.Drones
                .ToDictionary(item => item.DroneId, StringComparer.Ordinal);
            var initialSolids = initialStates.Values
                .SelectMany(item => item.Cells)
                .Where(cell => cell.Solid)
                .Select(ToBlock)
                .ToHashSet();

            foreach (var drone in mission.Drones.Values)
            {
                var telemetry = initialStates[drone.Id];
                var position = telemetry.Position!;
                drone.Position = position;
                drone.LastSeenTick = request.InitialFrame.GameTick;
                drone.LastSeenAt = Stopwatch.GetTimestamp();
                drone.LastContactTick = request.InitialFrame.GameTick;
                drone.DirectlyConnectedToTower = true;
                drone.ConnectedToTower = true;
                drone.KnownCells = telemetry.Cells.ToDictionary(
                    ToBlock, cell => new KnownCell(cell.Solid, request.InitialFrame.GameTick));
                mission.TowerPeerPositions[drone.Id] =
                    new(Block.Floor(position), CopyPosition(position), request.InitialFrame.GameTick);
                if (!CanCommunicate(Block.From(request.Tower), Block.Floor(position), initialSolids))
                    throw new ProtocolException(400, "initial_link_required", $"Drone '{drone.Id}' must have a clear direct link to the tower at mission start.");
            }

            mission.LastSequence = 0;
            mission.LastGameTick = request.InitialFrame.GameTick;
            mission.LastDecisionTick = request.InitialFrame.GameTick;
            mission.LastFreshAt = Stopwatch.GetTimestamp();
            mission.Version = 1;
            mission.LastResponse = new(1, mission.Id, 0, request.InitialFrame.GameTick, [], []);
            UpdateLinks(mission);
            foreach (var drone in mission.Drones.Values)
            foreach (var peerId in drone.ConnectedPeers)
            {
                var peerPosition = mission.Drones[peerId].Position!;
                drone.PeerPositions[peerId] = new(
                    Block.Floor(peerPosition),
                    CopyPosition(peerPosition),
                    request.InitialFrame.GameTick);
            }
            _mission = mission;
            _lastResetMissionId = null;
            return mission.LastResponse;
        }
    }

    public AcceptedFrameResponse AcceptFrame(string missionId, ObservationFrame frame, string requestHash)
    {
        lock (_gate)
        {
            var mission = RequireMission(missionId);
            if (frame.Sequence == mission.LastSequence)
            {
                if (mission.LastFrameHash == requestHash)
                    return mission.LastResponse;
                throw new ProtocolException(409, "changed_retry", "A retry must exactly match the latest accepted observation body.");
            }
            if (frame.Sequence != mission.LastSequence + 1)
                throw new ProtocolException(409, "sequence_conflict", "Observation sequence must be the next consecutive number.");
            if (frame.GameTick < mission.LastGameTick)
                throw new ProtocolException(400, "tick_decreased", "Game ticks must not decrease within a mission.");

            ValidateFrame(mission, frame);
            var previousTick = mission.LastGameTick;
            var wasFresh = IsFresh(mission);
            var telemetryById = frame.Drones.ToDictionary(item => item.DroneId, StringComparer.Ordinal);
            var acknowledged = new List<AcknowledgedResult>();

            foreach (var drone in mission.Drones.Values)
            {
                var telemetry = telemetryById[drone.Id];
                if (telemetry.Availability == "unavailable")
                {
                    drone.Faulted = true;
                    drone.ConnectedToTower = false;
                    drone.ConnectedPeers.Clear();
                    continue;
                }

                drone.Position = telemetry.Position;
                drone.LastSeenTick = frame.GameTick;
                drone.LastSeenAt = Stopwatch.GetTimestamp();
                foreach (var observation in telemetry.Cells)
                    MergeCell(drone.KnownCells, ToBlock(observation), observation.Solid, frame.GameTick);

                if (telemetry.CommandResult is { } result)
                {
                    acknowledged.Add(new(drone.Id, result.CommandId));
                    if (result.Status == "completed" && drone.OutstandingExploration)
                        drone.FrontierMovesSinceUpload++;
                    if (result.Status == "completed" && drone.OutstandingPeerSearch && drone.OutstandingDestination is { } peerSearchBlock)
                        drone.PeerSearchVisited.Add(peerSearchBlock);
                    if (result.Status == "completed" && drone.OutstandingRendezvousSearch && drone.OutstandingDestination is { } rendezvousBlock)
                        drone.RendezvousVisited.Add(rendezvousBlock);
                    drone.OutstandingExploration = false;
                    drone.OutstandingPeerSearch = false;
                    drone.OutstandingRendezvousSearch = false;
                    drone.FrontierTransit = false;
                    drone.OutstandingCommand = null;
                    if (result.Status == "failed")
                        drone.Faulted = true;
                    else if (result.Status == "blocked")
                        drone.BlockedMoves[(drone.OutstandingSource!.Value, drone.OutstandingDestination!.Value)] = frame.GameTick;
                    drone.OutstandingDestination = null;
                    drone.OutstandingSource = null;
                    drone.OutstandingSinceTick = null;
                }
            }

            mission.LastSequence = frame.Sequence;
            mission.LastGameTick = frame.GameTick;
            mission.LastFreshAt = Stopwatch.GetTimestamp();
            if (!wasFresh)
            {
                RebaseBehaviorTimers(mission, frame.GameTick - previousTick);
                mission.LastDecisionTick = frame.GameTick;
            }
            else if (frame.GameTick > previousTick && frame.GameTick - mission.LastDecisionTick >= 10)
            {
                mission.LastDecisionTick = frame.GameTick;
                RunDecisionCycle(mission);
            }

            mission.Version++;
            mission.LastFrameHash = requestHash;
            mission.LastResponse = new(
                1,
                mission.Id,
                frame.Sequence,
                frame.GameTick,
                acknowledged,
                mission.Drones.Values
                    .Where(drone => drone.OutstandingCommand is not null)
                    .OrderBy(drone => drone.Id, StringComparer.Ordinal)
                    .Select(drone => drone.OutstandingCommand!)
                    .ToArray());
            return mission.LastResponse;
        }
    }

    public void Reset(string missionId)
    {
        lock (_gate)
        {
            if (_mission is null)
            {
                if (_lastResetMissionId == missionId)
                    return;
                throw new ProtocolException(404, "mission_not_found", "No active mission matches this ID.");
            }
            if (_mission.Id != missionId)
                throw new ProtocolException(404, "mission_not_found", "No active mission matches this ID.");

            _retiredMissionIds.Add(_mission.Id);
            _lastResetMissionId = _mission.Id;
            _mission = null;
        }
    }

    public PublicSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (_mission is null)
                return new(1, null, 0, "awaitingMission", "disconnected", null, null, false, null, [], [], 0, [], null, [], null);

            var mission = _mission;
            var connected = IsFresh(mission);
            var liveCells = new Dictionary<Block, KnownCell>(mission.TowerCells);
            foreach (var drone in mission.Drones.Values)
                MergeCells(liveCells, drone.KnownCells);
            return new(
                1,
                mission.Id,
                mission.Version,
                mission.Phase,
                connected ? "connected" : "disconnected",
                mission.WorldId,
                mission.Tower.ToPoint(),
                mission.TargetReported,
                mission.TargetReported ? mission.Target.ToPoint() : null,
                ToPublicTerrain(mission.TowerCells),
                mission.Drones.Values
                    .OrderBy(drone => drone.Id, StringComparer.Ordinal)
                    .Select(drone => ToPublicDrone(mission, drone, connected))
                    .ToArray(),
                mission.Drones.Values.Count(drone => drone.Recovered && !drone.Faulted),
                mission.Drones.Values
                    .Where(drone => drone.Faulted ||
                        (mission.TargetReported && !drone.Recovered && (drone.Stuck || !drone.ConnectedToTower)))
                    .Select(drone => drone.Id)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                mission.LastGameTick,
                ToPublicTerrain(liveCells),
                mission.Target.ToPoint());
        }
    }

    private static PublicCell[] ToPublicTerrain(Dictionary<Block, KnownCell> cells) =>
        cells.Where(pair => pair.Value.Solid)
            .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z)
            .Select(pair => new PublicCell(pair.Key.X, pair.Key.Y, pair.Key.Z, pair.Value.SourceTick))
            .ToArray();

    public DroneDebug GetDroneDebug(string droneId)
    {
        lock (_gate)
        {
            var mission = _mission ?? throw new ProtocolException(404, "mission_not_found", "There is no active mission.");
            if (!mission.Drones.TryGetValue(droneId, out var drone))
                throw new ProtocolException(404, "drone_not_found", "No drone matches this ID.");
            return new(
                mission.Id,
                drone.Id,
                Behavior(mission, drone),
                drone.Position,
                drone.LastSeenTick,
                mission.LastGameTick,
                IsFresh(mission) ? AgeSeconds(drone) : null,
                ContactAgeSeconds(mission, drone),
                drone.RescueAssignment?.PeerId,
                drone.RendezvousPeerId,
                drone.ConnectedPeers.Order(StringComparer.Ordinal).ToArray(),
                drone.KnownCells
                    .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z)
                    .Select(pair => new DebugCell(pair.Key.X, pair.Key.Y, pair.Key.Z, pair.Value.Solid, pair.Value.SourceTick))
                    .ToArray(),
                drone.TargetKnown ? mission.Target.ToPoint() : null,
                drone.FrontierMovesSinceUpload);
        }
    }

    private static PublicDrone ToPublicDrone(Mission mission, Drone drone, bool fresh)
    {
        var hasKnownPosition = mission.TowerPeerPositions.TryGetValue(drone.Id, out var knownPosition);
        int? knownAgeSeconds = hasKnownPosition
            ? (int)Math.Max(0, mission.LastGameTick - knownPosition.SourceTick) / 20
            : null;
        return new(
            drone.Id,
            hasKnownPosition ? CopyPosition(knownPosition.Position) : null,
            drone.ConnectedToTower,
            drone.DirectlyConnectedToTower,
            fresh && drone.ConnectedToTower && hasKnownPosition && knownPosition.SourceTick == mission.LastGameTick,
            drone.Faulted,
            Behavior(mission, drone),
            drone.KnownCells.Count,
            hasKnownPosition ? knownPosition.SourceTick : null,
            fresh ? knownAgeSeconds : null,
            drone.ConnectedToTower
                ? drone.ConnectedPeers
                    .Where(peerId => mission.Drones[peerId].ConnectedToTower)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [],
            drone.Position is null ? null : CopyPosition(drone.Position),
            fresh && !drone.Faulted && drone.Position is not null && drone.LastSeenTick == mission.LastGameTick);
    }

    private static int? ContactAgeSeconds(Mission mission, Drone drone) =>
        drone.LastContactTick is null
            ? null
            : (int)Math.Max(0, mission.LastGameTick - drone.LastContactTick.Value) / 20;

    private static string Behavior(Mission mission, Drone drone)
    {
        if (!IsFresh(mission)) return "telemetryLost";
        if (drone.Faulted) return "faulted";
        if (drone.WaitingForClearance) return "waitingForClearance";
        if (drone.OutstandingSinceTick is not null && mission.LastGameTick - drone.OutstandingSinceTick >= 200)
            return "stalled";
        if (drone.Stuck) return "stuck";
        if (drone.RescueAssignment is not null) return "searchingForPeer";
        if (drone.RendezvousPeerId is not null) return "seekingPeer";
        if (mission.TargetReported && drone.Recovered) return "recovered";
        if (mission.Phase == "recoveryIncomplete" && !drone.Recovered) return "recoveryIncomplete";
        if (mission.Phase == "recovering" && drone.TargetKnown) return "returning";
        if (drone.ReturningToUpload) return "returningToUpload";
        return drone.OutstandingCommand is null ? "exploring" : "moving";
    }

    private static int? AgeSeconds(Drone drone) => drone.LastSeenTick is null
        ? null
        : (int)Math.Floor(Stopwatch.GetElapsedTime(drone.LastSeenAt).TotalSeconds);

    private static bool IsFresh(Mission mission) =>
        Stopwatch.GetElapsedTime(mission.LastFreshAt) <= TimeSpan.FromSeconds(3);

    private Mission RequireMission(string missionId)
    {
        if (_mission is null || _mission.Id != missionId)
            throw new ProtocolException(404, "mission_not_found", "No active mission matches this ID.");
        return _mission;
    }

    private static void ValidateInitialization(InitializeMissionRequest request)
    {
        if (request is null || request.BoundsMin is null || request.Tower is null ||
            request.Target is null || request.InitialFrame is null || request.InitialFrame.Drones is null)
            throw new ProtocolException(400, "invalid_mission", "Mission setup must include bounds, tower, target, and an initial drone frame.");
        if (request.ProtocolVersion != 1)
            throw new ProtocolException(400, "unsupported_protocol", "Only protocol version 1 is supported.");
        if (!Guid.TryParse(request.MissionId, out _))
            throw new ProtocolException(400, "invalid_mission_id", "Mission ID must be a UUID.");
        if (string.IsNullOrWhiteSpace(request.WorldId))
            throw new ProtocolException(400, "invalid_world_id", "World ID must be a non-empty string.");
        if (request.InitialFrame.Sequence != 0)
            throw new ProtocolException(400, "invalid_initial_sequence", "Initial frame sequence must be 0.");
        if (request.InitialFrame.Drones.Count == 0 || request.InitialFrame.Drones.Any(item => item is null))
            throw new ProtocolException(400, "empty_roster", "A mission requires at least one drone.");

        var bounds = request.BoundsMin;
        if (!IsInBounds(Block.From(request.Tower), Block.From(bounds)) ||
            !IsInBounds(Block.From(request.Target), Block.From(bounds)))
            throw new ProtocolException(400, "outside_bounds", "Tower and target must be inside the exploration bounds.");

        ValidateFrameContents(request.InitialFrame, Block.From(bounds), request.InitialFrame.Drones.Select(item => item.DroneId).ToHashSet(StringComparer.Ordinal), true);
        ValidateObservationAgreement(request.InitialFrame);
        foreach (var telemetry in request.InitialFrame.Drones)
            if (telemetry.Availability != "available" || telemetry.CommandResult is not null)
                throw new ProtocolException(400, "invalid_initial_drone", "Every initial drone must be available and have no command result.");

        var ticks = request.InitialFrame.Drones.Select(_ => request.InitialFrame.GameTick).Distinct().Count();
        if (ticks != 1 || request.InitialFrame.GameTick < 0)
            throw new ProtocolException(400, "invalid_initial_tick", "Initial telemetry must use one non-negative game tick.");
    }

    private static void ValidateFrame(Mission mission, ObservationFrame frame)
    {
        if (frame.Drones is null)
            throw new ProtocolException(400, "invalid_roster", "Drone telemetry must be an array.");
        if (frame.GameTick < 0)
            throw new ProtocolException(400, "invalid_tick", "Game tick must be non-negative.");
        ValidateFrameContents(frame, mission.BoundsMin, mission.Drones.Keys.ToHashSet(StringComparer.Ordinal), false);
        foreach (var telemetry in frame.Drones)
        {
            var drone = mission.Drones[telemetry.DroneId];
            if (telemetry.Availability == "unavailable")
            {
                if (string.IsNullOrWhiteSpace(telemetry.Reason) || telemetry.Position is not null || telemetry.Cells.Count != 0)
                    throw new ProtocolException(400, "invalid_unavailable_drone", "Unavailable drones require a reason and must not include position or cells.");
                if (telemetry.CommandResult is not null)
                    throw new ProtocolException(400, "invalid_command_result", "Unavailable drones cannot report a command result.");
                continue;
            }

            if (telemetry.CommandResult is not { } result)
                continue;
            if (drone.OutstandingCommand is null ||
                drone.OutstandingCommand.CommandId != result.CommandId ||
                result.Status is not ("completed" or "blocked" or "failed") ||
                (result.Status != "completed" && string.IsNullOrWhiteSpace(result.Reason)))
                throw new ProtocolException(400, "invalid_command_result", $"Drone '{drone.Id}' reported an unknown or invalid command result.");
        }

        ValidateObservationAgreement(frame);
    }

    private static void ValidateObservationAgreement(ObservationFrame frame)
    {
        var sameTickCells = new Dictionary<Block, bool>();
        foreach (var telemetry in frame.Drones.Where(item => item.Availability == "available"))
        foreach (var cell in telemetry.Cells)
        {
            var block = ToBlock(cell);
            if (sameTickCells.TryGetValue(block, out var solid) && solid != cell.Solid)
                throw new ProtocolException(400, "conflicting_observations", "Overlapping observations in a frame must agree.");
            sameTickCells[block] = cell.Solid;
        }
    }

    private static void ValidateFrameContents(
        ObservationFrame frame,
        Block boundsMin,
        HashSet<string> roster,
        bool initialization)
    {
        if (frame.Drones is null)
            throw new ProtocolException(400, "invalid_roster", "Drone telemetry must be an array.");
        if (frame.Drones.Count != roster.Count)
            throw new ProtocolException(400, "invalid_roster", "Every roster member must appear exactly once in the frame.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var telemetry in frame.Drones)
        {
            if (telemetry is null || telemetry.Cells is null)
                throw new ProtocolException(400, "invalid_drone", "Drone telemetry and cell arrays cannot be null.");
            if (string.IsNullOrWhiteSpace(telemetry.DroneId) ||
                !roster.Contains(telemetry.DroneId) ||
                !seen.Add(telemetry.DroneId))
                throw new ProtocolException(400, "invalid_roster", "Frame contains a missing, duplicate, or unknown drone ID.");
            if (telemetry.Availability is not ("available" or "unavailable"))
                throw new ProtocolException(400, "invalid_availability", $"Drone '{telemetry.DroneId}' has an invalid availability value.");
            if (telemetry.Availability == "unavailable")
            {
                if (initialization)
                    throw new ProtocolException(400, "invalid_initial_drone", "Initial drones must be available.");
                continue;
            }

            if (telemetry.Position is null ||
                !double.IsFinite(telemetry.Position.X) ||
                !double.IsFinite(telemetry.Position.Y) ||
                !double.IsFinite(telemetry.Position.Z))
                throw new ProtocolException(400, "invalid_position", $"Drone '{telemetry.DroneId}' requires finite position coordinates.");
            var block = Block.Floor(telemetry.Position);
            if (!IsInBounds(block, boundsMin))
                throw new ProtocolException(400, "outside_bounds", $"Drone '{telemetry.DroneId}' is outside the exploration bounds.");

            var expected = new HashSet<Block>();
            for (var x = Math.Max(boundsMin.X, block.X - 3); x <= Math.Min(boundsMin.X + Width - 1, block.X + 3); x++)
            for (var y = Math.Max(boundsMin.Y, block.Y - 3); y <= Math.Min(boundsMin.Y + Height - 1, block.Y + 3); y++)
            for (var z = Math.Max(boundsMin.Z, block.Z - 3); z <= Math.Min(boundsMin.Z + Depth - 1, block.Z + 3); z++)
                expected.Add(new(x, y, z));

            var observed = new HashSet<Block>();
            foreach (var cell in telemetry.Cells)
            {
                if (cell is null)
                    throw new ProtocolException(400, "invalid_cells", $"Drone '{telemetry.DroneId}' contains a null cell.");
                var coordinate = ToBlock(cell);
                if (!expected.Contains(coordinate) || !observed.Add(coordinate))
                    throw new ProtocolException(400, "invalid_cells", $"Drone '{telemetry.DroneId}' has an out-of-cube or duplicate cell.");
            }
            if (observed.Count != expected.Count)
                throw new ProtocolException(400, "incomplete_cube", $"Drone '{telemetry.DroneId}' must report its complete clipped 7x7x7 observation cube.");
        }
    }

    private static void RunDecisionCycle(Mission mission)
    {
        foreach (var drone in mission.Drones.Values)
        {
            if (drone.Position is null || drone.Faulted) continue;
            MarkVisibleCells(drone, mission.LastGameTick);
            if (!drone.TargetKnown && CanSeeTarget(mission, drone))
                drone.TargetKnown = true;
        }

        var oldDroneCells = mission.Drones.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<Block, KnownCell>(pair.Value.KnownCells),
            StringComparer.Ordinal);
        var oldDroneSearch = mission.Drones.ToDictionary(
            pair => pair.Key,
            pair => new HashSet<Block>(pair.Value.SearchedCells),
            StringComparer.Ordinal);
        var oldTargetKnowledge = mission.Drones.ToDictionary(pair => pair.Key, pair => pair.Value.TargetKnown, StringComparer.Ordinal);
        var oldRecallKnowledge = mission.Drones.ToDictionary(pair => pair.Key, pair => pair.Value.RecallKnown, StringComparer.Ordinal);
        var oldPeerPositions = mission.Drones.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, PeerPosition>(pair.Value.PeerPositions, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var oldKnownRescueAssignments = mission.Drones.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, RecoveryAssignment>(pair.Value.KnownRescueAssignments, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var oldTowerCells = new Dictionary<Block, KnownCell>(mission.TowerCells);
        var oldTowerSearch = new HashSet<Block>(mission.TowerSearchedCells);
        var oldTowerTarget = mission.TargetReported;
        var oldTowerRecall = mission.RecallIssued;
        var oldTowerAssignments = new Dictionary<string, RecoveryAssignment>(mission.TowerAssignments, StringComparer.Ordinal);
        var oldPeers = mission.Drones.ToDictionary(
            pair => pair.Key,
            pair => new HashSet<string>(pair.Value.ConnectedPeers, StringComparer.Ordinal),
            StringComparer.Ordinal);

        UpdateLinks(mission);
        foreach (var drone in mission.Drones.Values)
        {
            if (drone.DirectlyConnectedToTower)
            {
                MergeCells(drone.KnownCells, oldTowerCells);
                drone.SearchedCells.UnionWith(oldTowerSearch);
                MergeCells(mission.TowerCells, oldDroneCells[drone.Id]);
                mission.TowerSearchedCells.UnionWith(oldDroneSearch[drone.Id]);
                mission.TowerPeerPositions[drone.Id] =
                    new(Block.Floor(drone.Position!), CopyPosition(drone.Position!), mission.LastGameTick);
                foreach (var sighting in oldPeerPositions[drone.Id])
                    MergePeerPosition(mission.TowerPeerPositions, sighting.Key, sighting.Value);
                drone.FrontierMovesSinceUpload = 0;
                drone.ReturningToUpload = false;
                drone.TargetKnown |= oldTowerTarget;
                if (oldTargetKnowledge[drone.Id])
                    mission.TargetReported = true;
                drone.RecallKnown |= oldTowerRecall;
                foreach (var assignment in oldTowerAssignments)
                    drone.KnownRescueAssignments[assignment.Key] = assignment.Value;
            }
        }
        foreach (var pair in mission.Drones.Values)
        foreach (var peerId in pair.ConnectedPeers)
        {
            var peer = mission.Drones[peerId];
            MergeCells(pair.KnownCells, oldDroneCells[peerId]);
            pair.SearchedCells.UnionWith(oldDroneSearch[peerId]);
            pair.TargetKnown |= oldTargetKnowledge[peerId];
            pair.RecallKnown |= oldRecallKnowledge[peerId];
            foreach (var sighting in oldPeerPositions[peerId])
                MergePeerPosition(pair.PeerPositions, sighting.Key, sighting.Value);
            foreach (var assignment in oldKnownRescueAssignments[peerId])
                pair.KnownRescueAssignments.TryAdd(assignment.Key, assignment.Value);
        }

        foreach (var drone in mission.Drones.Values)
        foreach (var assignment in drone.KnownRescueAssignments.ToArray())
            if (!mission.TowerAssignments.TryGetValue(assignment.Key, out var activeAssignment) ||
                activeAssignment != assignment.Value)
                drone.KnownRescueAssignments.Remove(assignment.Key);

        foreach (var drone in mission.Drones.Values.Where(drone => drone.Position is not null && !drone.Faulted))
        {
            drone.RescueAssignment = drone.KnownRescueAssignments.GetValueOrDefault(drone.Id);
            if (drone.DirectlyConnectedToTower || drone.ConnectedPeers.Count > 0)
                drone.LastContactTick = mission.LastGameTick;
            foreach (var peerId in drone.ConnectedPeers)
            {
                var peer = mission.Drones[peerId];
                drone.PeerPositions[peerId] = new(
                    Block.Floor(peer.Position!),
                    CopyPosition(peer.Position!),
                    mission.LastGameTick);
            }
            if (drone.RescueAssignment is { } assignment &&
                drone.ConnectedPeers.Contains(assignment.PeerId))
            {
                mission.ExhaustedRescueSightings.Add((assignment.PeerId, assignment.SourceTick));
                mission.TowerAssignments.Remove(drone.Id);
                drone.KnownRescueAssignments.Remove(drone.Id);
                drone.RescueAssignment = null;
                drone.PeerSearchStartedTick = null;
                drone.PeerSearchVisited.Clear();
            }
            if (drone.DirectlyConnectedToTower)
            {
                ClearRendezvous(drone);
                drone.ReturnAfterRendezvous = false;
            }
            if (drone.ConnectedPeers.Count > 0 && drone.RendezvousPeerId is not null)
            {
                ClearRendezvous(drone);
                drone.ReturnAfterRendezvous = false;
            }
        }

        DeferUploadsOnEncounters(mission, oldPeers);

        if (mission.TargetReported && !mission.RecallIssued)
            mission.RecallIssued = true;
        if (mission.RecallIssued)
            foreach (var drone in mission.Drones.Values.Where(drone => drone.DirectlyConnectedToTower && oldTowerRecall))
                drone.RecallKnown = true;

        if (mission.TargetReported)
        {
            mission.Phase = "recovering";
            foreach (var drone in mission.Drones.Values.Where(drone => drone.Position is not null))
                drone.Recovered = IsInTowerNeighbourhood(mission, Block.Floor(drone.Position!)) &&
                    CanCommunicate(mission.Tower, Block.Floor(drone.Position!), SolidCells(drone.KnownCells, mission.TowerCells));
            if (mission.Drones.Values.All(drone => drone.Recovered && !drone.Faulted))
                mission.Phase = "complete";
            else
            {
                AssignRescuers(mission);
            }
        }
        else if (mission.Drones.Values.All(drone => drone.Faulted || drone.Position is null))
        {
            mission.Phase = "searchExhausted";
        }

        UpdateRendezvous(mission);
        PlanMoves(mission);
    }

    private static void DeferUploadsOnEncounters(Mission mission, Dictionary<string, HashSet<string>> oldPeers)
    {
        var eligible = mission.Drones.Values.Where(drone =>
            !drone.Faulted && drone.Position is not null && !drone.TargetKnown && !drone.RecallKnown &&
            drone.RescueAssignment is null && drone.RendezvousPeerId is null && !drone.ReturnAfterRendezvous)
            .ToDictionary(drone => drone.Id, drone => drone.FrontierMovesSinceUpload, StringComparer.Ordinal);
        foreach (var (id, moves) in eligible)
        foreach (var peerId in mission.Drones[id].ConnectedPeers)
        {
            if (StringComparer.Ordinal.Compare(id, peerId) >= 0 || oldPeers[id].Contains(peerId) ||
                !eligible.TryGetValue(peerId, out var peerMoves))
                continue;
            // Use pre-reset counters for simultaneous encounters; the lower ID keeps an equal schedule.
            var exploring = mission.Drones[moves < peerMoves ? id : peerId];
            if (exploring.ReturningToUpload || eligible[exploring.Id] >= FrontierMovesBeforeUpload)
                continue;
            exploring.FrontierMovesSinceUpload = 0;
        }
    }

    private static void RebaseBehaviorTimers(Mission mission, long elapsedTicks)
    {
        if (elapsedTicks <= 0) return;
        foreach (var drone in mission.Drones.Values)
        {
            if (drone.LastContactTick is { } lastContact)
                drone.LastContactTick = lastContact + elapsedTicks;
            if (drone.OutstandingSinceTick is { } outstandingSince)
                drone.OutstandingSinceTick = outstandingSince + elapsedTicks;
            if (drone.PeerSearchStartedTick is { } peerSearchStarted)
                drone.PeerSearchStartedTick = peerSearchStarted + elapsedTicks;
            if (drone.RendezvousStartedTick is { } rendezvousStarted)
                drone.RendezvousStartedTick = rendezvousStarted + elapsedTicks;
            foreach (var move in drone.BlockedMoves.Keys.ToArray())
                drone.BlockedMoves[move] += elapsedTicks;
        }
    }

    private static void UpdateLinks(Mission mission)
    {
        foreach (var drone in mission.Drones.Values)
        {
            drone.ConnectedPeers.Clear();
            drone.DirectlyConnectedToTower = drone.Position is not null && !drone.Faulted &&
                IsInTowerNeighbourhood(mission, Block.Floor(drone.Position)) &&
                CanCommunicate(mission.Tower, Block.Floor(drone.Position), SolidCells(drone.KnownCells, mission.TowerCells));
        }
        var active = mission.Drones.Values.Where(drone => drone.Position is not null && !drone.Faulted).ToArray();
        for (var i = 0; i < active.Length; i++)
        for (var j = i + 1; j < active.Length; j++)
        {
            var a = Block.Floor(active[i].Position!);
            var b = Block.Floor(active[j].Position!);
            if (!WithinCube(a, b) ||
                !CanCommunicate(a, b, SolidCells(active[i].KnownCells, active[j].KnownCells))) continue;
            active[i].ConnectedPeers.Add(active[j].Id);
            active[j].ConnectedPeers.Add(active[i].Id);
        }

        var connected = new HashSet<string>(
            mission.Drones.Values.Where(drone => drone.DirectlyConnectedToTower).Select(drone => drone.Id),
            StringComparer.Ordinal);
        var queue = new Queue<string>(connected);
        while (queue.TryDequeue(out var droneId))
            foreach (var peerId in mission.Drones[droneId].ConnectedPeers)
                if (connected.Add(peerId))
                    queue.Enqueue(peerId);
        foreach (var drone in mission.Drones.Values)
            drone.ConnectedToTower = connected.Contains(drone.Id);
    }

    private static void PlanMoves(Mission mission)
    {
        if (mission.Phase == "complete") return;
        var reserved = new HashSet<Block>();
        foreach (var drone in mission.Drones.Values)
        {
            if (drone.Position is not null) reserved.Add(Block.Floor(drone.Position));
            if (drone.OutstandingDestination is { } destination) reserved.Add(destination);
        }

        foreach (var drone in mission.Drones.Values.OrderBy(drone => drone.Id, StringComparer.Ordinal))
        {
            drone.Stuck = false;
            drone.WaitingForClearance = false;
            if (drone.Position is null || drone.Faulted || drone.OutstandingCommand is not null)
                continue;
            var start = Block.Floor(drone.Position);
            if (mission.Phase == "recovering" && drone.Recovered && drone.RescueAssignment is null)
                continue;

            if (drone.RescueAssignment is { } rescue)
            {
                var routeToPeer = PlanPeerSearch(drone, start, rescue.Position, mission, true);
                if (routeToPeer is null)
                {
                    mission.ExhaustedRescueSightings.Add((rescue.PeerId, rescue.SourceTick));
                    mission.TowerAssignments.Remove(drone.Id);
                    drone.KnownRescueAssignments.Remove(drone.Id);
                    drone.RescueAssignment = null;
                    drone.PeerSearchStartedTick = null;
                    drone.PeerSearchVisited.Clear();
                    drone.FrontierMovesSinceUpload = 0;
                }
                else if (routeToPeer.Count >= 2)
                {
                    if (reserved.Contains(routeToPeer[1]))
                        routeToPeer = FindPeerSearchRoute(drone, start, rescue.Position, drone.PeerSearchVisited, mission, reserved);
                    if (routeToPeer.Count >= 2)
                        IssueMove(drone, start, routeToPeer, false, true, false, mission, reserved);
                    else
                        drone.WaitingForClearance = true;
                    continue;
                }
            }

            if (drone.FrontierMovesSinceUpload >= FrontierMovesBeforeUpload)
                drone.ReturningToUpload = true;
            var returning = drone.TargetKnown || drone.RecallKnown || drone.ReturningToUpload || drone.ReturnAfterRendezvous;
            if (!returning && drone.RendezvousPeerId is not null && drone.RendezvousPosition is { } peerPosition)
            {
                var routeToPeer = PlanPeerSearch(drone, start, peerPosition.Block, mission, false);
                if (routeToPeer is null)
                {
                    ClearRendezvous(drone);
                    drone.ReturnAfterRendezvous = true;
                    returning = true;
                }
                else if (routeToPeer.Count >= 2)
                {
                    if (reserved.Contains(routeToPeer[1]))
                        routeToPeer = FindPeerSearchRoute(drone, start, peerPosition.Block, drone.RendezvousVisited, mission, reserved);
                    if (routeToPeer.Count >= 2)
                        IssueMove(drone, start, routeToPeer, false, false, true, mission, reserved);
                    else
                        drone.WaitingForClearance = true;
                    continue;
                }
            }

            var route = FindRoute(drone, start, returning ? mission.Tower : null, mission);
            if (!returning && route.Count < 2)
                route = FindFrontierRoute(drone, start, mission);
            if (route.Count < 2)
            {
                drone.Stuck = true;
                continue;
            }

            if (reserved.Contains(route[1]))
            {
                route = returning
                    ? FindRoute(drone, start, mission.Tower, mission, reserved)
                    : FindFrontierRoute(drone, start, mission, reserved);
                if (route.Count < 2)
                {
                    drone.WaitingForClearance = true;
                    continue;
                }
            }
            IssueMove(drone, start, route, !returning && route.Count == 2, false, false, mission, reserved);
        }

        if (!mission.TargetReported)
        {
            var activeDrones = mission.Drones.Values.Where(drone => !drone.Faulted && drone.Position is not null).ToArray();
            mission.Phase = activeDrones.All(drone => drone.Stuck) ? "searchExhausted" : "searching";
        }
        else if (mission.Drones.Values.All(drone => drone.Recovered && !drone.Faulted))
        {
            mission.Phase = "complete";
        }
        else if (mission.TowerAssignments.Count == 0 &&
                 mission.Drones.Values.Where(drone => !drone.Recovered && !drone.Faulted)
                     .All(drone => drone.Stuck && drone.OutstandingCommand is null))
        {
            mission.Phase = "recoveryIncomplete";
        }
        else
        {
            mission.Phase = "recovering";
        }
    }

    private static void IssueMove(
        Drone drone,
        Block start,
        IReadOnlyList<Block> route,
        bool exploration,
        bool peerSearch,
        bool rendezvousSearch,
        Mission mission,
        HashSet<Block> reserved)
    {
        var destination = route[1];
        reserved.Add(destination);
        drone.OutstandingSource = start;
        drone.OutstandingDestination = destination;
        drone.OutstandingSinceTick = mission.LastGameTick;
        drone.OutstandingExploration = exploration;
        drone.OutstandingPeerSearch = peerSearch;
        drone.OutstandingRendezvousSearch = rendezvousSearch;
        drone.FrontierTransit = exploration && route.Count > 2;
        drone.OutstandingCommand = new(drone.Id, Guid.NewGuid().ToString(), "move", destination.ToPoint());
    }

    private static List<Block>? PlanPeerSearch(Drone drone, Block start, Block peer, Mission mission, bool rescue)
    {
        var visited = rescue ? drone.PeerSearchVisited : drone.RendezvousVisited;
        var startedAt = rescue ? drone.PeerSearchStartedTick : drone.RendezvousStartedTick;
        if (IsWithinPeerSearchRegion(start, peer))
        {
            if (startedAt is null)
            {
                if (rescue) drone.PeerSearchStartedTick = mission.LastGameTick;
                else drone.RendezvousStartedTick = mission.LastGameTick;
                visited.Add(start);
            }
            else if (mission.LastGameTick - startedAt >= 300)
            {
                return null;
            }
        }

        var route = FindPeerSearchRoute(drone, start, peer, visited, mission);
        if (route.Count == 0) return null;
        if (route.Count == 1)
        {
            visited.Add(start);
            route = FindPeerSearchRoute(drone, start, peer, visited, mission);
            if (route.Count == 0) return null;
        }
        return route;
    }

    private static List<Block> FindPeerSearchRoute(
        Drone drone,
        Block start,
        Block peer,
        HashSet<Block> searched,
        Mission mission,
        IReadOnlySet<Block>? reserved = null)
    {
        var queue = new Queue<Block>();
        var previous = new Dictionary<Block, Block>();
        var distance = new Dictionary<Block, int> { [start] = 0 };
        queue.Enqueue(start);
        var candidates = new List<Block>();
        int? candidateDistance = null;

        while (queue.TryDequeue(out var current))
        {
            var currentDistance = distance[current];
            if (candidateDistance is not null && currentDistance > candidateDistance.Value)
                break;
            if (IsWithinPeerSearchRegion(current, peer) && !searched.Contains(current))
            {
                candidateDistance = currentDistance;
                candidates.Add(current);
                continue;
            }
            foreach (var next in Neighbours(current))
            {
                if (!IsInBounds(next, mission.BoundsMin) ||
                    (current == start && reserved?.Contains(next) == true) ||
                    !drone.KnownCells.TryGetValue(next, out var cell) ||
                    cell.Solid ||
                    IsMoveBlocked(drone, current, next, mission.LastGameTick) ||
                    distance.ContainsKey(next))
                    continue;
                distance[next] = currentDistance + 1;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        if (candidates.Count == 0) return [];
        var chosen = candidates
            .OrderBy(block => block.X)
            .ThenBy(block => block.Y)
            .ThenBy(block => block.Z)
            .First();
        var route = new List<Block> { chosen };
        while (route[^1] != start)
            route.Add(previous[route[^1]]);
        route.Reverse();
        return route;
    }

    private static bool IsWithinPeerSearchRegion(Block viewpoint, Block peer) =>
        Math.Abs(viewpoint.X - peer.X) <= 7 &&
        Math.Abs(viewpoint.Y - peer.Y) <= 7 &&
        Math.Abs(viewpoint.Z - peer.Z) <= 7;

    private static void UpdateRendezvous(Mission mission)
    {
        if (mission.TargetReported || mission.Phase != "searching") return;
        foreach (var drone in mission.Drones.Values)
        {
            if (drone.Position is null || drone.Faulted || drone.RescueAssignment is not null ||
                drone.TargetKnown || drone.RecallKnown || drone.RendezvousPeerId is not null ||
                drone.FrontierTransit || drone.ReturnAfterRendezvous ||
                drone.LastContactTick is null || mission.LastGameTick - drone.LastContactTick < 600)
                continue;

            var sighting = drone.PeerPositions
                .Where(pair => pair.Key != drone.Id)
                .OrderByDescending(pair => pair.Value.SourceTick)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (KeyValuePair<string, PeerPosition>?)pair)
                .FirstOrDefault();
            if (sighting is null)
            {
                drone.ReturnAfterRendezvous = true;
                continue;
            }
            drone.RendezvousPeerId = sighting.Value.Key;
            drone.RendezvousPosition = sighting.Value.Value;
            drone.RendezvousStartedTick = null;
            drone.RendezvousVisited.Clear();
        }
    }

    private static void AssignRescuers(Mission mission)
    {
        foreach (var assignment in mission.TowerAssignments.ToArray())
        {
            if (!mission.Drones.TryGetValue(assignment.Key, out var rescuer) ||
                rescuer.Faulted || rescuer.Position is null)
            {
                mission.TowerAssignments.Remove(assignment.Key);
                continue;
            }
            if (!mission.Drones.TryGetValue(assignment.Value.PeerId, out var peer)) continue;
            if (peer.Recovered && !peer.Faulted)
            {
                mission.TowerAssignments.Remove(assignment.Key);
                mission.ExhaustedRescueSightings.Add((assignment.Value.PeerId, assignment.Value.SourceTick));
            }
        }

        foreach (var peer in mission.Drones.Values
                     .Where(drone => drone.Faulted || !drone.ConnectedToTower)
                     .OrderBy(drone => drone.Id, StringComparer.Ordinal))
        {
            if (!mission.TowerPeerPositions.TryGetValue(peer.Id, out var sighting) ||
                mission.ExhaustedRescueSightings.Contains((peer.Id, sighting.SourceTick)) ||
                mission.TowerAssignments.Values.Any(assignment => assignment.PeerId == peer.Id))
                continue;

            var rescuer = mission.Drones.Values
                .Where(drone => drone.Id != peer.Id && !drone.Faulted &&
                    drone.Position is not null && drone.ConnectedToTower &&
                    !mission.TowerAssignments.ContainsKey(drone.Id))
                .OrderBy(drone => drone.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (rescuer is null) continue;

            mission.TowerAssignments[rescuer.Id] =
                new(rescuer.Id, peer.Id, sighting.Block, sighting.SourceTick);
        }
    }

    private static List<Block> FindRoute(Drone drone, Block start, Block? goal, Mission mission, IReadOnlySet<Block>? reserved = null)
    {
        if (goal is null || start == goal.Value) return [];
        var localSolids = drone.KnownCells.Where(pair => pair.Value.Solid).Select(pair => pair.Key).ToHashSet();
        var solidHive = goal.Value == mission.Tower && localSolids.Contains(mission.Tower);
        var queue = new Queue<Block>();
        var previous = new Dictionary<Block, Block>();
        var visited = new HashSet<Block> { start };
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            foreach (var next in Neighbours(current))
            {
                if (!IsInBounds(next, mission.BoundsMin) ||
                    (current == start && reserved?.Contains(next) == true) ||
                    !drone.KnownCells.TryGetValue(next, out var cell) ||
                    cell.Solid ||
                    IsMoveBlocked(drone, current, next, mission.LastGameTick) ||
                    !visited.Add(next))
                    continue;
                previous[next] = current;
                if (next == goal.Value || (solidHive && IsInTowerNeighbourhood(mission, next) &&
                    CanCommunicate(mission.Tower, next, localSolids)))
                {
                    var path = new List<Block> { next };
                    while (path[^1] != start)
                        path.Add(previous[path[^1]]);
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(next);
            }
        }
        return [];
    }

    private static List<Block> FindFrontierRoute(Drone drone, Block start, Mission mission, IReadOnlySet<Block>? reserved = null)
    {
        var queue = new Queue<Block>();
        var previous = new Dictionary<Block, Block>();
        var distance = new Dictionary<Block, int> { [start] = 0 };
        queue.Enqueue(start);
        var candidates = new List<(Block Block, int Distance, int Gain)>();
        var localSolids = drone.KnownCells.Where(pair => pair.Value.Solid).Select(pair => pair.Key).ToHashSet();
        if (localSolids.Count == 0) return [];
        var startsNearSurface = IsNearSurface(start, localSolids);
        int? nearestCandidateDistance = null;

        while (queue.TryDequeue(out var current))
        {
            if (nearestCandidateDistance is not null && distance[current] > nearestCandidateDistance.Value)
                break;
            var nearSurface = IsNearSurface(current, localSolids);
            var gain = nearSurface
                ? EstimateNewCoverage(drone, current, localSolids, mission)
                : 0;
            if (!startsNearSurface && nearSurface)
                gain = Math.Max(1, gain);
            if (gain > 0)
            {
                nearestCandidateDistance = distance[current];
                candidates.Add((current, distance[current], gain));
            }
            if (nearestCandidateDistance is not null)
                continue;

            foreach (var next in Neighbours(current))
            {
                if (!IsInBounds(next, mission.BoundsMin) ||
                    (current == start && reserved?.Contains(next) == true) ||
                    !drone.KnownCells.TryGetValue(next, out var cell) ||
                    cell.Solid ||
                    (startsNearSurface && !IsNearSurface(next, localSolids)) ||
                    IsMoveBlocked(drone, current, next, mission.LastGameTick) ||
                    distance.ContainsKey(next))
                    continue;
                distance[next] = distance[current] + 1;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        var chosen = candidates
            .OrderBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => candidate.Gain)
            .ThenBy(candidate => candidate.Block.X)
            .ThenBy(candidate => candidate.Block.Y)
            .ThenBy(candidate => candidate.Block.Z)
            .FirstOrDefault();
        if (chosen.Gain == 0) return [];

        var route = new List<Block> { chosen.Block };
        while (route[^1] != start)
            route.Add(previous[route[^1]]);
        route.Reverse();
        return route;
    }

    private static bool IsNearSurface(Block position, HashSet<Block> solids)
    {
        for (var x = -SurfaceDistance; x <= SurfaceDistance; x++)
        for (var y = -SurfaceDistance; y <= SurfaceDistance; y++)
        for (var z = -SurfaceDistance; z <= SurfaceDistance; z++)
            if (x * x + y * y + z * z <= SurfaceDistance * SurfaceDistance &&
                solids.Contains(new(position.X + x, position.Y + y, position.Z + z)))
                return true;
        return false;
    }

    private static int EstimateNewCoverage(Drone drone, Block viewpoint, HashSet<Block> solids, Mission mission)
    {
        var gain = 0;
        for (var x = viewpoint.X - 3; x <= viewpoint.X + 3; x++)
        for (var y = viewpoint.Y - 3; y <= viewpoint.Y + 3; y++)
        for (var z = viewpoint.Z - 3; z <= viewpoint.Z + 3; z++)
        {
            var cell = new Block(x, y, z);
            if (IsInBounds(cell, mission.BoundsMin) &&
                (!drone.KnownCells.ContainsKey(cell) || !drone.SearchedCells.Contains(cell)) &&
                !SegmentBlocked(viewpoint, cell, solids))
                gain++;
        }
        return gain;
    }

    private static bool CanSeeTarget(Mission mission, Drone drone)
    {
        if (drone.Position is null) return false;
        var from = Block.Floor(drone.Position);
        var target = mission.Target;
        if (!WithinCube(from, target)) return false;
        var solids = drone.KnownCells.Where(pair => pair.Value.Solid).Select(pair => pair.Key).ToHashSet();
        return !SegmentBlocked(from, target, solids);
    }

    private static void MarkVisibleCells(Drone drone, long tick)
    {
        if (drone.Position is null) return;
        var from = Block.Floor(drone.Position);
        var solids = drone.KnownCells.Where(pair => pair.Value.Solid).Select(pair => pair.Key).ToHashSet();
        for (var x = from.X - 3; x <= from.X + 3; x++)
        for (var y = from.Y - 3; y <= from.Y + 3; y++)
        for (var z = from.Z - 3; z <= from.Z + 3; z++)
            if (!SegmentBlocked(from, new(x, y, z), solids))
                drone.SearchedCells.Add(new(x, y, z));
    }

    private static bool CanCommunicate(Block from, Block to, HashSet<Block> solids) =>
        WithinCube(from, to) && !SegmentBlocked(from, to, solids);

    private static bool WithinCube(Block from, Block to) =>
        Math.Abs(from.X - to.X) <= 3 &&
        Math.Abs(from.Y - to.Y) <= 3 &&
        Math.Abs(from.Z - to.Z) <= 3;

    private static bool SegmentBlocked(Block from, Block to, HashSet<Block> solids)
    {
        if (from == to) return false;
        // Bound the broad phase; exact intersections retain edge/corner occlusion.
        for (var x = Math.Min(from.X, to.X); x <= Math.Max(from.X, to.X); x++)
        for (var y = Math.Min(from.Y, to.Y); y <= Math.Max(from.Y, to.Y); y++)
        for (var z = Math.Min(from.Z, to.Z); z <= Math.Max(from.Z, to.Z); z++)
        {
            var solid = new Block(x, y, z);
            if (solid == from || solid == to || !solids.Contains(solid)) continue;
            if (SegmentIntersectsCell(from, to, solid)) return true;
        }
        return false;
    }

    private static bool SegmentIntersectsCell(Block from, Block to, Block cell)
    {
        var tMin = 0d;
        var tMax = 1d;
        return IntersectAxis(from.X + 0.5, to.X - from.X, cell.X, ref tMin, ref tMax) &&
               IntersectAxis(from.Y + 0.5, to.Y - from.Y, cell.Y, ref tMin, ref tMax) &&
               IntersectAxis(from.Z + 0.5, to.Z - from.Z, cell.Z, ref tMin, ref tMax) &&
               tMax >= 0 && tMin <= 1;
    }

    private static bool IntersectAxis(double origin, double direction, int cell, ref double tMin, ref double tMax)
    {
        var min = cell - 1e-9;
        var max = cell + 1 + 1e-9;
        if (Math.Abs(direction) < 1e-12)
            return origin >= min && origin <= max;
        var a = (min - origin) / direction;
        var b = (max - origin) / direction;
        if (a > b) (a, b) = (b, a);
        tMin = Math.Max(tMin, a);
        tMax = Math.Min(tMax, b);
        return tMin <= tMax;
    }

    private static bool IsInTowerNeighbourhood(Mission mission, Block block) => WithinCube(mission.Tower, block);

    private static IEnumerable<Block> Neighbours(Block block)
    {
        yield return new(block.X + 1, block.Y, block.Z);
        yield return new(block.X - 1, block.Y, block.Z);
        yield return new(block.X, block.Y + 1, block.Z);
        yield return new(block.X, block.Y - 1, block.Z);
        yield return new(block.X, block.Y, block.Z + 1);
        yield return new(block.X, block.Y, block.Z - 1);
    }

    private static bool IsInBounds(Block block, Block min) =>
        block.X >= min.X && block.X < min.X + Width &&
        block.Y >= min.Y && block.Y < min.Y + Height &&
        block.Z >= min.Z && block.Z < min.Z + Depth;

    private static Block ToBlock(CellObservation cell) => new(cell.X, cell.Y, cell.Z);

    private static void MergeCell(Dictionary<Block, KnownCell> cells, Block coordinate, bool solid, long tick)
    {
        if (!cells.TryGetValue(coordinate, out var old) || tick >= old.SourceTick)
            cells[coordinate] = new(solid, tick);
    }

    private static void MergeCells(Dictionary<Block, KnownCell> destination, Dictionary<Block, KnownCell> source)
    {
        foreach (var pair in source)
            if (!destination.TryGetValue(pair.Key, out var old) || pair.Value.SourceTick >= old.SourceTick)
                destination[pair.Key] = pair.Value;
    }

    private static void MergePeerPosition(
        Dictionary<string, PeerPosition> destination,
        string peerId,
        PeerPosition sighting)
    {
        if (!destination.TryGetValue(peerId, out var old) || sighting.SourceTick >= old.SourceTick)
            destination[peerId] = sighting;
    }

    private static DronePosition CopyPosition(DronePosition position) =>
        new() { X = position.X, Y = position.Y, Z = position.Z };

    private static void ClearRendezvous(Drone drone)
    {
        drone.RendezvousPeerId = null;
        drone.RendezvousPosition = null;
        drone.RendezvousStartedTick = null;
        drone.RendezvousVisited.Clear();
        drone.ReturnAfterRendezvous = false;
    }

    private static HashSet<Block> SolidCells(Dictionary<Block, KnownCell> first, Dictionary<Block, KnownCell> second) =>
        first.Where(pair => pair.Value.Solid).Select(pair => pair.Key)
            .Concat(second.Where(pair => pair.Value.Solid).Select(pair => pair.Key))
            .ToHashSet();

    private static bool IsMoveBlocked(Drone drone, Block from, Block to, long currentTick) =>
        drone.BlockedMoves.TryGetValue((from, to), out var blockedTick) && currentTick - blockedTick < 100;

    private sealed class Mission
    {
        public Mission(InitializeMissionRequest request, string initialHash)
        {
            Id = request.MissionId;
            WorldId = request.WorldId;
            BoundsMin = Block.From(request.BoundsMin);
            Tower = Block.From(request.Tower);
            Target = Block.From(request.Target);
            InitialHash = initialHash;
            Drones = request.InitialFrame.Drones.ToDictionary(
                item => item.DroneId,
                item => new Drone(item.DroneId),
                StringComparer.Ordinal);
        }

        public string Id { get; }
        public string WorldId { get; }
        public Block BoundsMin { get; }
        public Block Tower { get; }
        public Block Target { get; }
        public string InitialHash { get; }
        public Dictionary<string, Drone> Drones { get; }
        public Dictionary<Block, KnownCell> TowerCells { get; } = [];
        public HashSet<Block> TowerSearchedCells { get; } = [];
        public long Version { get; set; }
        public long LastSequence { get; set; }
        public long LastGameTick { get; set; }
        public long LastDecisionTick { get; set; }
        public long LastFreshAt { get; set; }
        public string? LastFrameHash { get; set; }
        public AcceptedFrameResponse LastResponse { get; set; } = new(1, "", 0, 0, [], []);
        public string Phase { get; set; } = "searching";
        public bool TargetReported { get; set; }
        public bool RecallIssued { get; set; }
        public Dictionary<string, PeerPosition> TowerPeerPositions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RecoveryAssignment> TowerAssignments { get; } = new(StringComparer.Ordinal);
        public HashSet<(string PeerId, long SourceTick)> ExhaustedRescueSightings { get; } = [];
    }

    private sealed class Drone(string id)
    {
        public string Id { get; } = id;
        public DronePosition? Position { get; set; }
        public bool WaitingForClearance { get; set; }
        public long? LastSeenTick { get; set; }
        public long LastSeenAt { get; set; }
        public Dictionary<Block, KnownCell> KnownCells { get; set; } = [];
        public HashSet<Block> SearchedCells { get; } = [];
        public HashSet<string> ConnectedPeers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PeerPosition> PeerPositions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RecoveryAssignment> KnownRescueAssignments { get; } = new(StringComparer.Ordinal);
        public HashSet<Block> PeerSearchVisited { get; } = [];
        public HashSet<Block> RendezvousVisited { get; } = [];
        public Dictionary<(Block From, Block To), long> BlockedMoves { get; } = [];
        public MovementCommand? OutstandingCommand { get; set; }
        public Block? OutstandingDestination { get; set; }
        public Block? OutstandingSource { get; set; }
        public long? OutstandingSinceTick { get; set; }
        public bool ConnectedToTower { get; set; }
        public bool DirectlyConnectedToTower { get; set; }
        public bool Faulted { get; set; }
        public bool TargetKnown { get; set; }
        public bool RecallKnown { get; set; }
        public bool Recovered { get; set; }
        public bool Stuck { get; set; }
        public int FrontierMovesSinceUpload { get; set; }
        public bool ReturningToUpload { get; set; }
        public bool OutstandingExploration { get; set; }
        public bool OutstandingPeerSearch { get; set; }
        public bool OutstandingRendezvousSearch { get; set; }
        public bool FrontierTransit { get; set; }
        public long? LastContactTick { get; set; }
        public long? PeerSearchStartedTick { get; set; }
        public RecoveryAssignment? RescueAssignment { get; set; }
        public string? RendezvousPeerId { get; set; }
        public PeerPosition? RendezvousPosition { get; set; }
        public long? RendezvousStartedTick { get; set; }
        public bool ReturnAfterRendezvous { get; set; }
    }
}
