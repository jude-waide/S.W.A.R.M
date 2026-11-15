using System.Text.Json;

namespace DroneSwarm.Web;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}

public sealed class InitializeMissionRequest
{
    public int ProtocolVersion { get; set; }
    public string MissionId { get; set; } = "";
    public string WorldId { get; set; } = "";
    public BlockPoint BoundsMin { get; set; } = new();
    public BlockPoint Tower { get; set; } = new();
    public BlockPoint Target { get; set; } = new();
    public ObservationFrame InitialFrame { get; set; } = new();
    public bool ContinuousFlight { get; set; }
}

public sealed class ObservationFrame
{
    public long Sequence { get; set; }
    public long GameTick { get; set; }
    public List<DroneTelemetry> Drones { get; set; } = [];
}

public sealed class DroneTelemetry
{
    public string DroneId { get; set; } = "";
    public string Availability { get; set; } = "";
    public string? Reason { get; set; }
    public DronePosition? Position { get; set; }
    public List<CellObservation> Cells { get; set; } = [];
    public CommandResult? CommandResult { get; set; }
}

public sealed class DronePosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class BlockPoint : IEquatable<BlockPoint>
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    public bool Equals(BlockPoint? other) =>
        other is not null && X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is BlockPoint other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
}

public sealed class CellObservation
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public bool Solid { get; set; }
}

public sealed class CommandResult
{
    public string CommandId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
}

public sealed record AcceptedFrameResponse(
    int ProtocolVersion,
    string MissionId,
    long Sequence,
    long GameTick,
    IReadOnlyList<AcknowledgedResult> AcknowledgedResults,
    IReadOnlyList<MovementCommand> Commands);

public sealed record AcknowledgedResult(string DroneId, string CommandId);
public sealed record MovementCommand(string DroneId, string CommandId, string Kind, BlockPoint Destination,
    string? AfterCommandId = null);
public sealed record MotionSnapshot(string MissionId, long Version, long GameTick, IReadOnlyList<LiveDrone> Drones);
public sealed record LiveDrone(string Id, DronePosition? Position, bool PositionCurrent, bool Faulted);
public sealed record PluginStatus(int ProtocolVersion, string? ActiveMissionId, string Phase);

public sealed record PublicSnapshot(
    int ProtocolVersion,
    string? MissionId,
    long Version,
    string Phase,
    string ConnectionState,
    string? WorldId,
    BlockPoint? Tower,
    bool TargetReported,
    BlockPoint? ReportedTarget,
    IReadOnlyList<PublicCell> Terrain,
    IReadOnlyList<PublicDrone> Drones,
    int RecoveredCount,
    IReadOnlyList<string> MissingDroneIds,
    long? GameTick,
    IReadOnlyList<PublicCell> LiveTerrain,
    BlockPoint? OverallTarget,
    bool TargetFound);

public sealed record ReplayIndex(string? MissionId, IReadOnlyList<long> GameTicks);

public sealed record PublicCell(int X, int Y, int Z, long SourceTick);
public sealed record PublicDrone(
    string Id,
    DronePosition? Position,
    bool ConnectedToTower,
    bool DirectlyConnectedToTower,
    bool PositionCurrent,
    bool Faulted,
    string Behavior,
    int KnownCellCount,
    long? LastSeenTick,
    int? LastReportAgeSeconds,
    IReadOnlyList<string> ConnectedPeers,
    DronePosition? LivePosition,
    bool LivePositionCurrent);

public sealed record DroneDebug(
    string MissionId,
    string DroneId,
    string Behavior,
    DronePosition? Position,
    long? LastSeenTick,
    long? GameTick,
    int? LastReportAgeSeconds,
    int? LastContactAgeSeconds,
    string? RecoveryTargetId,
    string? RendezvousTargetId,
    IReadOnlyList<string> ConnectedPeers,
    IReadOnlyList<DebugCell> Cells,
    BlockPoint? KnownTarget,
    int FrontierMovesSinceUpload,
    IReadOnlyList<PeerSighting> PeerPositions);

public sealed record DebugCell(int X, int Y, int Z, bool Solid, long SourceTick);
public sealed record PeerSighting(string DroneId, DronePosition Position, long SourceTick);

public sealed class ProtocolException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

internal readonly record struct Block(int X, int Y, int Z)
{
    public static Block From(BlockPoint point) => new(point.X, point.Y, point.Z);
    public BlockPoint ToPoint() => new() { X = X, Y = Y, Z = Z };
    public static Block Floor(DronePosition position) =>
        new((int)Math.Floor(position.X), (int)Math.Floor(position.Y), (int)Math.Floor(position.Z));
}

internal sealed record KnownCell(bool Solid, long SourceTick);
internal readonly record struct PeerPosition(Block Block, DronePosition Position, long SourceTick);
internal sealed record RecoveryAssignment(string RescuerId, string PeerId, Block Position, long SourceTick);
