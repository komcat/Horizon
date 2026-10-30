using Horizon.Engine;

namespace Horizon.Workflow.Scanning;

/// <summary>
/// The motion half of the FireDog-ported scans (XCAN, climb): single-axis relative /
/// absolute moves and full-pose moves on one device, each time-bounded by its own
/// timeout on top of the run token, plus live pose reads. Any failure throws
/// <see cref="ScanMotionException"/> — the scan unwinds to its fail-safe park, exactly
/// as the Lua <c>error()</c> did. Never fabricates a pose: an unreadable position aborts.
/// </summary>
internal sealed class ScanAxisMotion
{
    private readonly IMotionFacet _motion;
    private readonly int _moveTimeoutMs;

    public ScanAxisMotion(IMotionFacet motion, string device, int moveTimeoutMs)
    {
        _motion = motion;
        Device = device;
        _moveTimeoutMs = Math.Clamp(moveTimeoutMs, 100, 600_000);
    }

    public string Device { get; }

    public async Task<MotionPosition> PoseAsync()
    {
        var r = await _motion.GetPositionAsync(Device).ConfigureAwait(false);
        if (!r.IsSuccess)
            throw new ScanMotionException($"cannot read {Device} position ({r.Message}) — aborting to avoid an unsafe move");
        return r.Value;
    }

    public async Task<double> AxisAsync(string axis) => Component(await PoseAsync().ConfigureAwait(false), axis);

    public Task MoveRelativeAsync(string axis, double deltaMm, CancellationToken ct)
        => TimedAsync(t => _motion.MoveRelativeAsync(Device, axis, deltaMm, t), $"{axis} {deltaMm * 1000:+0.###;-0.###} µm", ct);

    public Task MoveAxisToAsync(string axis, double targetMm, CancellationToken ct)
        => TimedAsync(t => _motion.MoveAbsoluteAsync(Device, axis, targetMm, t), $"{axis} → {targetMm:F5}", ct);

    public Task MoveToAsync(MotionPosition pose, CancellationToken ct)
        => TimedAsync(t => _motion.MoveToPositionAsync(Device, pose, t),
            $"pose X{pose.X:F5} Y{pose.Y:F5} Z{pose.Z:F5}", ct);

    private async Task TimedAsync(Func<CancellationToken, Task<Horizon.Common.Result>> move, string what, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_moveTimeoutMs);
        var r = await move(cts.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.IsSuccess)
            throw new ScanMotionException(cts.IsCancellationRequested
                ? $"{Device} move {what} timed out after {_moveTimeoutMs} ms"
                : $"{Device} move {what} failed: {r.Message}");
    }

    /// <summary>The coordinate of <paramref name="axis"/> (X..W) in <paramref name="p"/>.</summary>
    public static double Component(MotionPosition p, string axis) => axis.Trim().ToUpperInvariant() switch
    {
        "X" => p.X,
        "Y" => p.Y,
        "Z" => p.Z,
        "U" => p.U,
        "V" => p.V,
        "W" => p.W,
        _ => throw new ArgumentException($"unknown axis '{axis}'", nameof(axis)),
    };

    /// <summary>True for a valid axis name (X, Y, Z, U, V, W).</summary>
    public static bool IsAxis(string axis) => axis.Trim().ToUpperInvariant() is "X" or "Y" or "Z" or "U" or "V" or "W";
}

/// <summary>A scan move / pose read failed — the scan aborts to its fail-safe park.</summary>
public sealed class ScanMotionException : Exception
{
    public ScanMotionException(string message) : base(message) { }
}
