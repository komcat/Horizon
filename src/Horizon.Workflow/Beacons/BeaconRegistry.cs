using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A virtual marker planted where the camera centre was: a point in the camera device's
/// (gantry-main) axis values that the live views draw as a small + mark, wherever the camera
/// has moved since. <see cref="ColorIndex"/> picks its colour from the views' fixed palette.
///
/// <para>A beacon may <b>ride on a tool</b> (<see cref="ToolDevice"/> set — the marker of a gripped
/// object, <c>object.register</c>): <see cref="X"/>/<see cref="Y"/> are then where it was while the
/// tool stood at <see cref="ToolPose"/>, and its live place is that point carried by the tool's
/// move since, through the tool's registration (<c>IProcessService.LocateBeacon</c>).</para>
/// </summary>
public sealed record Beacon(
    string Name, string Device, double X, double Y, double Z, int ColorIndex, bool Visible,
    DateTimeOffset PlantedAt, DateTimeOffset ExpiresAt,
    string? ToolDevice = null, MotionPosition? ToolPose = null, BeaconOutline? Outline = null)
{
    /// <summary>True when the beacon moves with a tool device instead of staying on the machine.</summary>
    public bool RidesOnTool => !string.IsNullOrEmpty(ToolDevice) && ToolPose != null;
}

/// <summary>
/// A construction line between two beacons (<c>beacon.line</c>), kept by the beacons' names: the
/// live views draw it between wherever the two beacons are now, and it goes when either beacon does.
/// </summary>
public sealed record BeaconLine(string Name, string From, string To, bool Visible)
{
    /// <summary>
    /// Length (mm), direction and centre of the segment (x1, y1) → (x2, y2), all in the camera
    /// device's axis values. <c>AngleDeg</c> uses the Detect Object <b>P</b> convention: 0° = the
    /// line points straight up on the image, clockwise positive, −180…+180 (so it is comparable
    /// with a block's <c>_p</c>); the image directions come from the MoveClick axis signs.
    /// </summary>
    public static (double Length, double AngleDeg, double Cx, double Cy) Measure(
        double x1, double y1, double x2, double y2, AppSettings s)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double ix = dx * (s.MoveClickXSign < 0 ? -1 : 1);   // image right
        double iy = dy * (s.MoveClickYSign < 0 ? -1 : 1);   // image down
        double angle = Math.Atan2(ix, -iy) * 180.0 / Math.PI;
        return (Math.Sqrt(dx * dx + dy * dy), DetectObjectMath.Wrap(angle), (x1 + x2) / 2.0, (y1 + y2) / 2.0);
    }
}

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
    private readonly List<BeaconLine> _lines = new();  // oldest first; pruned with their beacons
    private int _nextColor;

    /// <summary>At most this many construction lines; one more replaces the oldest.</summary>
    public const int MaxLines = 10;

    /// <summary>
    /// Places a beacon that rides on a tool (set by <c>ProcessService</c>, which owns the tool
    /// registrations). Without it only fixed beacons can be placed.
    /// </summary>
    public Func<Beacon, (double X, double Y)?>? Locator { get; set; }

    /// <summary>Where a beacon is right now (camera device axis values); null when it cannot be placed.</summary>
    public (double X, double Y)? Locate(Beacon beacon)
        => Locator != null ? Locator(beacon) : beacon.RidesOnTool ? null : (beacon.X, beacon.Y);

    /// <summary>The live beacon by that name, or null.</summary>
    public Beacon? Find(string? name)
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.Now);
            return _beacons.Find(b => string.Equals(b.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The construction lines whose two beacons both still exist, oldest first.</summary>
    public IReadOnlyList<BeaconLine> Lines()
    {
        lock (_lock)
        {
            Prune(DateTimeOffset.Now);
            return _lines.Count == 0 ? Array.Empty<BeaconLine>() : _lines.ToArray();
        }
    }

    /// <summary>Adds a line between two beacons; an existing line of the same name is replaced.</summary>
    public BeaconLine AddLine(string name, string from, string to, bool visible)
    {
        var line = new BeaconLine(name, from, to, visible);
        lock (_lock)
        {
            _lines.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (_lines.Count >= MaxLines) _lines.RemoveAt(0);
            _lines.Add(line);
        }
        Changed?.Invoke();
        return line;
    }

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
    public Beacon Plant(string? name, string device, double x, double y, double z, bool visible, out string? replaced,
        string? toolDevice = null, MotionPosition? toolPose = null, BeaconOutline? outline = null)
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

            beacon = new Beacon(key, device, x, y, z, color, visible, now, now + Lifetime, toolDevice, toolPose, outline);
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
            // The same block also removes a construction line by its name.
            removed |= _lines.RemoveAll(l => string.Equals(l.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
            Prune(DateTimeOffset.Now);
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
            _lines.Clear();
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

    private void Prune(DateTimeOffset now)
    {
        _beacons.RemoveAll(b => b.ExpiresAt <= now);
        if (_lines.Count > 0)
            _lines.RemoveAll(l => !Exists(l.From) || !Exists(l.To));

        bool Exists(string name) => _beacons.Exists(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

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
