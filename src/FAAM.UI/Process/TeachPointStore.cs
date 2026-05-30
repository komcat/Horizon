using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Process;

/// <summary>
/// Reads the legacy FAAM teach points (<c>faam_teach_points.json</c>): a reusable
/// position name (Home, FAU, PIC, UV…) → taught <see cref="MotionPosition"/>.
/// Retained as the one-time migration source — named positions now live in the
/// Layer 2 product (<c>positions.json</c>) via <c>IProcessService</c>. Name
/// lookups are case-insensitive.
/// </summary>
public sealed class TeachPointStore
{
    public const string FileName = "faam_teach_points";

    private readonly Dictionary<string, MotionPosition> _points;

    public TeachPointStore()
    {
        var loaded = ConfigLoader.LoadJson<Dictionary<string, MotionPosition>>(
            ConfigLoader.GetConfigDirectory(), FileName);
        _points = loaded != null
            ? new Dictionary<string, MotionPosition>(loaded, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MotionPosition>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All legacy teach points (name → pose) — the migration source.</summary>
    public IReadOnlyDictionary<string, MotionPosition> All => _points;

    /// <summary>The taught pose for <paramref name="name"/>, or null if not taught.</summary>
    public MotionPosition? Get(string name) => _points.TryGetValue(name, out var p) ? p : null;

    public bool TryGet(string name, out MotionPosition pose) => _points.TryGetValue(name, out pose!);

    /// <summary>Stores (or replaces) a position's pose and persists immediately.</summary>
    public void Set(string name, MotionPosition pose)
    {
        _points[name] = pose;
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, _points);
    }
}
