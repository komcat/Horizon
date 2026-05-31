namespace Horizon.Workflow;

/// <summary>One saved revision of a process program — a full snapshot stamped with
/// who saved it and when. Keyed for diffing by the per-block <see cref="WorkflowStep.Id"/>.</summary>
public sealed record ProgramVersion
{
    public int Version { get; init; }
    public DateTime SavedUtc { get; init; }
    public string? OperatorId { get; init; }
    public string? OperatorName { get; init; }
    public string? StationId { get; init; }
    /// <summary>The full program as saved at this revision.</summary>
    public WorkflowProgram Snapshot { get; init; } = new();
}

/// <summary>The revision history of one program — newest version is last.</summary>
public sealed record ProgramHistory
{
    public string Program { get; init; } = string.Empty;
    public List<ProgramVersion> Versions { get; init; } = new();

    /// <summary>The most recent saved snapshot, or null if none.</summary>
    public ProgramVersion? Latest => Versions.Count == 0 ? null : Versions[^1];
}

/// <summary>
/// Filters for a cross-program history query (all fields optional / AND-combined).
/// An empty query returns the whole history (capped). Used by
/// <see cref="IProcessService.QueryHistory"/>.
/// </summary>
public sealed record HistoryQuery
{
    /// <summary>Exact block UID (e.g. <c>blk-…</c>).</summary>
    public string? BlockUid { get; init; }
    /// <summary>Exact parameter key (e.g. <c>distance</c>).</summary>
    public string? ParamKey { get; init; }
    /// <summary>Exact device / lane.</summary>
    public string? Device { get; init; }
    /// <summary>Exact operator id.</summary>
    public string? OperatorId { get; init; }
    /// <summary>Exact product.</summary>
    public string? Product { get; init; }
    /// <summary>Exact program name.</summary>
    public string? Program { get; init; }
}

/// <summary>One matched parameter value of a block in a revision — a history query result row.</summary>
public sealed record HistoryRow(
    string Product, string Program, int Version, DateTime SavedUtc,
    string? OperatorName, string BlockUid, string Op, string Device,
    string Key, string Value);

/// <summary>How a block changed between two revisions.</summary>
public enum BlockChangeKind { Added, Removed, Changed }

/// <summary>
/// A single block's change between two program revisions, identified by its stable
/// UID. <see cref="Details"/> lists the field-level changes for a
/// <see cref="BlockChangeKind.Changed"/> block (e.g. <c>distance: 1 → 2</c>).
/// </summary>
public sealed record BlockChange(
    string Uid,
    BlockChangeKind Kind,
    string Label,
    IReadOnlyList<string> Details);

/// <summary>Per-block (UID-keyed) diff between two program revisions.</summary>
public static class ProgramDiff
{
    /// <summary>
    /// Compares two programs by block UID and returns one <see cref="BlockChange"/>
    /// per added / removed / changed leaf block. <c>flow.parallel</c> containers are
    /// walked into (their branches' leaf blocks are compared); the container itself
    /// is not reported.
    /// </summary>
    public static IReadOnlyList<BlockChange> Compute(WorkflowProgram? before, WorkflowProgram? after)
    {
        var prev = Flatten(before);
        var curr = Flatten(after);
        var changes = new List<BlockChange>();

        foreach (var (uid, step) in curr)
        {
            if (!prev.TryGetValue(uid, out var old))
            {
                changes.Add(new BlockChange(uid, BlockChangeKind.Added, Label(step), new[] { Label(step) }));
                continue;
            }
            var details = FieldDiffs(old, step);
            if (details.Count > 0)
                changes.Add(new BlockChange(uid, BlockChangeKind.Changed, Label(step), details));
        }

        foreach (var (uid, step) in prev)
            if (!curr.ContainsKey(uid))
                changes.Add(new BlockChange(uid, BlockChangeKind.Removed, Label(step), new[] { Label(step) }));

        return changes;
    }

    /// <summary>Flattens a program to its leaf blocks (recursing into parallel branches), keyed by UID.</summary>
    private static Dictionary<string, WorkflowStep> Flatten(WorkflowProgram? program)
    {
        var map = new Dictionary<string, WorkflowStep>(StringComparer.Ordinal);
        if (program == null) return map;

        void Walk(IEnumerable<WorkflowStep> steps)
        {
            foreach (var s in steps)
            {
                if (s.Op.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase) && s.Branches.Count > 0)
                    foreach (var b in s.Branches) Walk(b.Steps);
                else if (!string.IsNullOrEmpty(s.Id))
                    map[s.Id] = s;
            }
        }
        Walk(program.Steps);
        return map;
    }

    private static string Label(WorkflowStep s)
        => string.IsNullOrWhiteSpace(s.Device) ? s.Op : $"{s.Op} ({s.Device})";

    private static List<string> FieldDiffs(WorkflowStep a, WorkflowStep b)
    {
        var diffs = new List<string>();
        if (!string.Equals(a.Op, b.Op, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"op: {a.Op} → {b.Op}");
        if (!string.Equals(a.Device, b.Device, StringComparison.Ordinal))
            diffs.Add($"device: {Show(a.Device)} → {Show(b.Device)}");
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            diffs.Add($"name: {Show(a.Name)} → {Show(b.Name)}");
        if (a.Enabled != b.Enabled)
            diffs.Add($"enabled: {a.Enabled} → {b.Enabled}");

        // Parameter changes (added / removed / changed keys).
        foreach (var key in a.Parameters.Keys.Union(b.Parameters.Keys, StringComparer.Ordinal))
        {
            a.Parameters.TryGetValue(key, out var av);
            b.Parameters.TryGetValue(key, out var bv);
            if (!string.Equals(av, bv, StringComparison.Ordinal))
                diffs.Add($"{key}: {Show(av)} → {Show(bv)}");
        }
        return diffs;
    }

    private static string Show(string? v) => string.IsNullOrEmpty(v) ? "∅" : v;
}
