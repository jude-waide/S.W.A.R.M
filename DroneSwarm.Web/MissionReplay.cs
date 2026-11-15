namespace DroneSwarm.Web;

// Keep terrain material changes, not a copy of the accumulated map at every tick.
internal sealed class MissionReplay(string missionId)
{
    private sealed record Frame(PublicSnapshot Snapshot, PublicCell[] HiveChanges, PublicCell[] OverallChanges);
    private readonly List<Frame> _frames = [];
    private readonly Dictionary<Block, PublicCell> _hive = [];
    private readonly Dictionary<Block, PublicCell> _overall = [];
    public string MissionId { get; } = missionId;

    public void Record(PublicSnapshot snapshot)
    {
        if (_frames.Count > 0 && _frames[^1].Snapshot.Phase == "complete") return;
        _frames.Add(new(
            snapshot with { Terrain = [], LiveTerrain = [] },
            Changes(_hive, snapshot.Terrain),
            Changes(_overall, snapshot.LiveTerrain)));
    }

    public ReplayIndex GetIndex(bool recordingEnabled) =>
        new(MissionId, _frames.Select(frame => frame.Snapshot.GameTick!.Value).ToArray(), recordingEnabled);

    public PublicSnapshot GetFrame(int index)
    {
        if (index < 0 || index >= _frames.Count)
            throw new ProtocolException(400, "invalid_replay_frame", "Replay frame is outside the recorded range.");
        var hive = new Dictionary<Block, PublicCell>();
        var overall = new Dictionary<Block, PublicCell>();
        for (var frame = 0; frame <= index; frame++)
        {
            Apply(hive, _frames[frame].HiveChanges);
            Apply(overall, _frames[frame].OverallChanges);
        }
        return _frames[index].Snapshot with { Terrain = hive.Values.ToArray(), LiveTerrain = overall.Values.ToArray() };
    }

    private static PublicCell[] Changes(Dictionary<Block, PublicCell> previous, IReadOnlyList<PublicCell> current)
    {
        var changes = new List<PublicCell>();
        var present = new HashSet<Block>();
        foreach (var cell in current)
        {
            var block = new Block(cell.X, cell.Y, cell.Z);
            present.Add(block);
            if (previous.TryAdd(block, cell)) changes.Add(cell);
        }
        foreach (var block in previous.Keys.Where(block => !present.Contains(block)).ToArray())
        {
            previous.Remove(block);
            changes.Add(new(block.X, block.Y, block.Z, -1));
        }
        return changes.ToArray();
    }

    private static void Apply(Dictionary<Block, PublicCell> terrain, PublicCell[] changes)
    {
        foreach (var cell in changes)
        {
            var block = new Block(cell.X, cell.Y, cell.Z);
            if (cell.SourceTick < 0) terrain.Remove(block);
            else terrain[block] = cell;
        }
    }
}
