namespace Horizon.Workflow;

/// <summary>One saved revision of a vision profile — a full snapshot stamped with who
/// saved it and when, keyed by the profile's stable <see cref="VisionProfile.Uid"/>.</summary>
public sealed record VisionProfileVersion
{
    public int Version { get; init; }
    public DateTime SavedUtc { get; init; }
    public string? OperatorId { get; init; }
    public string? OperatorName { get; init; }
    public string? StationId { get; init; }
    /// <summary>The full profile as saved at this revision.</summary>
    public VisionProfile Snapshot { get; init; } = new();
}

/// <summary>The revision history of one vision profile (by UID) — newest version is last.</summary>
public sealed record VisionProfileHistory
{
    public string ProfileUid { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public List<VisionProfileVersion> Versions { get; init; } = new();

    /// <summary>The most recent saved snapshot, or null if none.</summary>
    public VisionProfileVersion? Latest => Versions.Count == 0 ? null : Versions[^1];
}
