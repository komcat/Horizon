namespace Horizon.Workflow;

/// <summary>
/// Where a <c>vision.detectObject</c> block last found its object — shown for a short while on
/// the live views of <see cref="CameraId"/>. <see cref="X"/>/<see cref="Y"/> are axis values of
/// <see cref="Device"/> (the camera's motion device), so the mark stays on the object while the
/// camera moves. <see cref="P"/>: 0° = up on the image, clockwise positive.
/// </summary>
public sealed record DetectionMark(
    string Label, string CameraId, string Device, double X, double Y, double P,
    double Score, bool Manual, DateTimeOffset At)
{
    /// <summary>What was matched (<see cref="DetectedObject.Outline"/>), in <see cref="Device"/> axis values; null = none.</summary>
    public IReadOnlyList<IReadOnlyList<OutlinePoint>>? Outline { get; init; }

    /// <summary>How long a mark stays on the live views.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);

    /// <summary>0 = just found … 1 = about to disappear.</summary>
    public double Age(DateTimeOffset now) => Math.Clamp((now - At).TotalSeconds / Lifetime.TotalSeconds, 0, 1);
}

/// <summary>
/// The recent Detect Object results, in memory only: a display aid, never read by a program.
/// At most <see cref="MaxMarks"/> (the oldest goes first); each expires after
/// <see cref="DetectionMark.Lifetime"/>. Thread-safe — written by the run, read by the UI.
/// </summary>
internal sealed class DetectionMarkRegistry
{
    public const int MaxMarks = 8;

    private readonly object _gate = new();
    private readonly List<DetectionMark> _marks = new();   // oldest first

    public void Add(DetectionMark mark)
    {
        lock (_gate)
        {
            Prune(mark.At);
            if (_marks.Count >= MaxMarks) _marks.RemoveAt(0);
            _marks.Add(mark);
        }
    }

    public IReadOnlyList<DetectionMark> Snapshot()
    {
        lock (_gate)
        {
            Prune(DateTimeOffset.Now);
            return _marks.Count == 0 ? Array.Empty<DetectionMark>() : _marks.ToArray();
        }
    }

    private void Prune(DateTimeOffset now)
        => _marks.RemoveAll(m => now - m.At >= DetectionMark.Lifetime);
}
