using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A virtual marker planted where the camera centre was: a point in the camera device's
/// (gantry-main) axis values that the live views draw as a pulsing circle, wherever the camera
/// has moved since. <see cref="ColorIndex"/> picks its colour from the views' fixed palette.
/// </summary>
public sealed record Beacon(
    string Name, string Device, double X, double Y, double Z, int ColorIndex, bool Visible,
    DateTimeOffset PlantedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// The beacons planted by <c>beacon.plant</c> / removed by <c>beacon.remove</c>. In memory only
/// (never saved, gone when the application exits), machine-wide, and independent of the program
/// that planted them: a beacon lives <see cref="Lifetime"/> unless it is removed first. At most
/// <see cref="MaxBeacons"/> exist at once — planting one more replaces the oldest. Thread-safe.
/// See <c>docs/beacon-design.md</c>.
/// </summary>
public sealed class BeaconRegistry
{
    public const int MaxBeacons = 10;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly object _lock = new();
    private readonly List<Beacon> _beacons = new();   // oldest first
    private int _nextColor;

    /// <summary>Raised (on the caller's thread) after a beacon is planted, replaced or removed.</summary>
    public event Action? Changed;

    /// <summary>The live beacons, oldest first. Expired ones are dropped on the way.</summary>
    public IReadOnlyList<Beacon> Snapshot()
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.Now);
            return _beacons.ToArray();
        }
    }

    /// <summary>
    /// Plants a beacon at a point. A blank <paramref name="name"/> gets the next free
    /// <c>B1</c>, <c>B2</c>…; an existing name is moved (it keeps its colour, and its 30 minutes
    /// start again). <paramref name="replaced"/> names the oldest beacon when it had to make room.
    /// </summary>
    public Beacon Plant(string? name, string device, double x, double y, double z, bool visible, out string? replaced)
    {
        Beacon beacon;
        replaced = null;
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            Prune(now);

            string key = string.IsNullOrWhiteSpace(name) ? NextFreeName() : name.Trim();
            int at = _beacons.FindIndex(b => string.Equals(b.Name, key, StringComparison.OrdinalIgnoreCase));
            int color;
            if (at >= 0)
            {
                color = _beacons[at].ColorIndex;
                _beacons.RemoveAt(at);
            }
            else
            {
                if (_beacons.Count >= MaxBeacons)
                {
                    replaced = _beacons[0].Name;
                    _beacons.RemoveAt(0);
                }
                color = _nextColor;
                _nextColor = (_nextColor + 1) % MaxBeacons;
            }

            beacon = new Beacon(key, device, x, y, z, color, visible, now, now + Lifetime);
            _beacons.Add(beacon);
        }
        Changed?.Invoke();
        return beacon;
    }

    /// <summary>Removes one beacon by name; false when there is none by that name.</summary>
    public bool Remove(string name)
    {
        bool removed;
        lock (_lock)
        {
            removed = _beacons.RemoveAll(b => string.Equals(b.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Removes every beacon; returns how many there were.</summary>
    public int Clear()
    {
        int count;
        lock (_lock)
        {
            count = _beacons.Count;
            _beacons.Clear();
        }
        if (count > 0) Changed?.Invoke();
        return count;
    }

    /// <summary>
    /// Plants a beacon where the camera centre is now: reads the camera device's live position
    /// (<see cref="AppSettings.MoveClickDevice"/>, gantry-main) and plants there.
    /// </summary>
    public async Task<Result<Beacon>> PlantHereAsync(
        IHorizonEngine engine, AppSettings settings, string? name, bool visible, Action<LogLevel, string> log)
    {
        string device = string.IsNullOrWhiteSpace(settings.MoveClickDevice) ? "gantry-main" : settings.MoveClickDevice;
        var pose = await engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!pose.IsSuccess)
            return Result<Beacon>.Fail(pose.Error, $"Plant beacon: read {device} — {pose.Message}");
        var p = pose.Value;
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
            return Result<Beacon>.Fail(ErrorCode.OperationFailed, $"Plant beacon: {device} reported an invalid position.");

        var beacon = Plant(name, device, p.X, p.Y, p.Z, visible, out var replaced);
        if (replaced != null)
            log(LogLevel.Warning, $"Beacon limit ({MaxBeacons}) reached — the oldest, '{replaced}', was replaced.");
        log(LogLevel.Info,
            $"Beacon '{beacon.Name}' planted at {device} X {p.X:F4}  Y {p.Y:F4}  Z {p.Z:F4}" +
            (visible ? string.Empty : " (hidden)") + $" — lives {Lifetime.TotalMinutes:0} min.");
        return Result<Beacon>.Ok(beacon);
    }

    private void Prune(DateTimeOffset now) => _beacons.RemoveAll(b => b.ExpiresAt <= now);

    private string NextFreeName()
    {
        for (int i = 1; i <= MaxBeacons + 1; i++)
        {
            string candidate = "B" + i;
            if (!_beacons.Exists(b => string.Equals(b.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return "B" + (MaxBeacons + 1);
    }
}
