namespace Horizon.Workflow;

/// <summary>One saved revision of a deck — a full snapshot stamped with who saved it
/// and when. Keyed for diffing by the per-entry <see cref="DeckEntry.Uid"/>.</summary>
public sealed record DeckVersion
{
    public int Version { get; init; }
    public DateTime SavedUtc { get; init; }
    public string? OperatorId { get; init; }
    public string? OperatorName { get; init; }
    public string? StationId { get; init; }
    /// <summary>The full deck as saved at this revision.</summary>
    public WorkflowDeck Snapshot { get; init; } = new();
}

/// <summary>The revision history of one deck (by UID) — newest version is last.</summary>
public sealed record DeckHistory
{
    public string DeckUid { get; init; } = string.Empty;
    public string DeckName { get; init; } = string.Empty;
    public List<DeckVersion> Versions { get; init; } = new();

    /// <summary>The most recent saved snapshot, or null if none.</summary>
    public DeckVersion? Latest => Versions.Count == 0 ? null : Versions[^1];
}

/// <summary>
/// Filters for a cross-deck history query (all fields optional / AND-combined). An
/// empty query returns the whole history (capped). Used by
/// <see cref="IProcessService.QueryDeckHistory"/>.
/// </summary>
public sealed record DeckHistoryQuery
{
    /// <summary>Exact deck UID (e.g. <c>deck-…</c>).</summary>
    public string? DeckUid { get; init; }
    /// <summary>Exact entry UID (e.g. <c>dge-…</c>).</summary>
    public string? EntryUid { get; init; }
    /// <summary>Exact referenced program name.</summary>
    public string? Program { get; init; }
    /// <summary>Exact operator id.</summary>
    public string? OperatorId { get; init; }
    /// <summary>Exact product.</summary>
    public string? Product { get; init; }
}

/// <summary>One matched deck entry in a revision — a deck-history query result row.</summary>
public sealed record DeckHistoryRow(
    string Product, string DeckName, int Version, DateTime SavedUtc,
    string? OperatorName, string EntryUid, int Position, string Program,
    bool Auto, double DelaySeconds, string Condition);

/// <summary>How a deck entry changed between two revisions.</summary>
public enum EntryChangeKind { Added, Removed, Changed }

/// <summary>
/// A single entry's change between two deck revisions, identified by its stable UID.
/// <see cref="Details"/> lists the field-level changes for a
/// <see cref="EntryChangeKind.Changed"/> entry (e.g. <c>delay: 2 → 5</c>).
/// </summary>
public sealed record EntryChange(
    string Uid,
    EntryChangeKind Kind,
    string Label,
    IReadOnlyList<string> Details);

/// <summary>Per-entry (UID-keyed) diff between two deck revisions.</summary>
public static class DeckDiff
{
    /// <summary>
    /// Compares two decks by entry UID and returns one <see cref="EntryChange"/> per
    /// added / removed / changed entry (program reference, transition fields, position).
    /// Entries without a UID (legacy) are ignored.
    /// </summary>
    public static IReadOnlyList<EntryChange> Compute(WorkflowDeck? before, WorkflowDeck? after)
    {
        var prev = Index(before);
        var curr = Index(after);
        var changes = new List<EntryChange>();

        foreach (var (uid, e) in curr)
        {
            if (!prev.TryGetValue(uid, out var old))
            {
                changes.Add(new EntryChange(uid, EntryChangeKind.Added, Label(e.Entry), new[] { Label(e.Entry) }));
                continue;
            }
            var details = FieldDiffs(old, e);
            if (details.Count > 0)
                changes.Add(new EntryChange(uid, EntryChangeKind.Changed, Label(e.Entry), details));
        }

        foreach (var (uid, e) in prev)
            if (!curr.ContainsKey(uid))
                changes.Add(new EntryChange(uid, EntryChangeKind.Removed, Label(e.Entry), new[] { Label(e.Entry) }));

        return changes;
    }

    private readonly record struct Indexed(DeckEntry Entry, int Position);

    private static Dictionary<string, Indexed> Index(WorkflowDeck? deck)
    {
        var map = new Dictionary<string, Indexed>(StringComparer.Ordinal);
        if (deck == null) return map;
        for (int i = 0; i < deck.Entries.Count; i++)
        {
            var e = deck.Entries[i];
            if (!string.IsNullOrEmpty(e.Uid))
                map[e.Uid] = new Indexed(e, i + 1);
        }
        return map;
    }

    private static string Label(DeckEntry e) => string.IsNullOrWhiteSpace(e.Program) ? "(no program)" : e.Program;

    private static List<string> FieldDiffs(Indexed a, Indexed b)
    {
        var diffs = new List<string>();
        if (!string.Equals(a.Entry.Program, b.Entry.Program, StringComparison.Ordinal))
            diffs.Add($"program: {Show(a.Entry.Program)} → {Show(b.Entry.Program)}");
        if (a.Position != b.Position)
            diffs.Add($"position: {a.Position} → {b.Position}");
        if (a.Entry.Enabled != b.Entry.Enabled)
            diffs.Add($"enabled: {a.Entry.Enabled} → {b.Entry.Enabled}");
        if (a.Entry.Auto != b.Entry.Auto)
            diffs.Add($"auto: {a.Entry.Auto} → {b.Entry.Auto}");
        if (a.Entry.DelaySeconds != b.Entry.DelaySeconds)
            diffs.Add($"delay: {a.Entry.DelaySeconds:0.##} → {b.Entry.DelaySeconds:0.##}");
        if (!string.Equals(a.Entry.Condition, b.Entry.Condition, StringComparison.Ordinal))
            diffs.Add($"when: {Show(a.Entry.Condition)} → {Show(b.Entry.Condition)}");
        if (a.Entry.ShowInRun != b.Entry.ShowInRun)
            diffs.Add($"runPage: {a.Entry.ShowInRun} → {b.Entry.ShowInRun}");
        return diffs;
    }

    private static string Show(string? v) => string.IsNullOrEmpty(v) ? "∅" : v;
}
