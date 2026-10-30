using System.Globalization;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Scanning;

namespace Horizon.Workflow.Xcan;

/// <summary>
/// Settings of the two-lens XCAN sequence (FireDog <c>xcan_two_lens.lua</c>). Signal levels
/// are in the feedback signal's unit (amps for a Keithley current).
/// </summary>
public sealed record XcanTwoLensParameters
{
    public string LeftDevice { get; init; } = "hex-left";
    public string RightDevice { get; init; } = "hex-right";

    /// <summary>The target: a pass whose exit fires, or a fresh reading at or above it afterwards, ends the sequence.</summary>
    public double Target { get; init; }

    /// <summary>Adaptive zone low edge — must equal the hand-off signal level, never below it.</summary>
    public double ZoneLow { get; init; }

    /// <summary>Adaptive zone high edge; 0 = the target.</summary>
    public double ZoneHigh { get; init; }

    public IReadOnlyList<double> CoarseStepsMm { get; init; } = new[] { 0.010, 0.005, 0.002, 0.001 };
    public IReadOnlyList<double> FineStepsMm { get; init; } = new[] { 0.002, 0.001, 0.0005 };

    /// <summary>Everything else of a pass (axes, settle, velocity, trace…); the ladder, adaptive zones and exit are set per pass.</summary>
    public XcanParameters Pass { get; init; } = new();

    public Result Validate()
    {
        if (!ScanLimits.Positive(Target))
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN two-lens: the target must be > 0 — the sequence has nothing to reach.");
        double high = ZoneHigh > 0 ? ZoneHigh : Target;
        if (!(ScanLimits.Positive(ZoneLow) && ZoneLow < high))
            return Result.Fail(ErrorCode.InvalidParameter,
                $"XCAN two-lens: zone low ({ZoneLow:G4}) must be > 0 and below the zone high ({high:G4}).");
        if (string.IsNullOrWhiteSpace(LeftDevice) || string.IsNullOrWhiteSpace(RightDevice))
            return Result.Fail(ErrorCode.InvalidParameter, "XCAN two-lens: both hexapods must be named.");
        return Result.Ok();
    }
}

/// <summary>The outcome of the two-lens sequence.</summary>
public sealed record XcanTwoLensResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Reached { get; init; }
    public int Passes { get; init; }

    /// <summary>The label of the last pass run: "left coarse" | "left coarse 2" | "left fine" | "right coarse".</summary>
    public string Last { get; init; } = string.Empty;

    /// <summary>The last pass's confirming reading.</summary>
    public double Best { get; init; }

    /// <summary>A fresh reading after the last pass.</summary>
    public double Live { get; init; }
}

/// <summary>
/// The two-lens XCAN sequence: a FIXED order of up to four adaptive passes, each exiting at the
/// target, and the sequence ends the moment one reaches it —
/// <list type="number">
///   <item>LEFT coarse ladder</item>
///   <item>LEFT coarse ladder again</item>
///   <item>LEFT fine ladder</item>
///   <item>RIGHT coarse ladder</item>
/// </list>
/// then exits as is: the stages stay where the last pass left them. "Reached" is the pass's own
/// exit flag OR a fresh reading at or above the target after it. Every pass is
/// <see cref="XcanEngine"/>, so the scan has one implementation; this class only decides which
/// hexapod, which ladder and in what order. A pass that fails (or a STOP) ends the sequence
/// with that pass's fail-safe park already done.
/// </summary>
public sealed class XcanTwoLens
{
    private readonly XcanEngine _xcan;
    private readonly Action<LogLevel, string> _log;

    public XcanTwoLens(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _log = log ?? ((_, _) => { });
        _xcan = new XcanEngine(engine, _log);
    }

    public async Task<XcanTwoLensResult> RunAsync(
        IScanFeedbackSource feedback, XcanTwoLensParameters p, CancellationToken ct)
    {
        var valid = p.Validate();
        if (!valid.IsSuccess) return new XcanTwoLensResult { Message = valid.Message };

        double high = p.ZoneHigh > 0 ? p.ZoneHigh : p.Target;
        var sequence = new (string Label, string Device, IReadOnlyList<double> Steps)[]
        {
            ("left coarse", p.LeftDevice, p.CoarseStepsMm),
            ("left coarse 2", p.LeftDevice, p.CoarseStepsMm),
            ("left fine", p.LeftDevice, p.FineStepsMm),
            ("right coarse", p.RightDevice, p.CoarseStepsMm),
        };

        int passes = 0;
        string last = string.Empty;
        double best = 0, live = 0;
        foreach (var (label, device, steps) in sequence)
        {
            passes++;
            last = label;
            _log(LogLevel.Info,
                $"XCAN two-lens: {label} on {device}, ladder {string.Join(",", steps.Select(s => (s * 1000).ToString("0.###", CultureInfo.InvariantCulture)))} µm, " +
                $"adaptive [{p.ZoneLow:G4}, {high:G4}], target {p.Target:G4} {feedback.Unit}");

            var pass = p.Pass with
            {
                StepsMm = steps,
                Adaptive = true,
                ZoneLow = p.ZoneLow,
                ZoneHigh = high,
                ExitThreshold = p.Target,
            };
            var r = await _xcan.RunAsync(device, feedback, pass, ct).ConfigureAwait(false);
            if (!r.Success)
                return new XcanTwoLensResult
                {
                    Message = $"XCAN two-lens: {label} — {r.Message}", Passes = passes, Last = label, Best = r.Best,
                };

            best = r.Best;
            (live, _) = await FreshSampler.ReadAsync(feedback, p.Pass.FreshSamples, p.Pass.SettleMinMs,
                p.Pass.SettleCeilingMs, ct).ConfigureAwait(false);
            bool reached = r.ExitHit || live >= p.Target;
            _log(LogLevel.Info,
                $"XCAN two-lens: {label} done — best {best:G4}, live {live:G4} {feedback.Unit}, exit {(r.ExitHit ? "hit" : "not hit")}");
            if (reached)
                return new XcanTwoLensResult
                {
                    Success = true, Reached = true, Passes = passes, Last = label, Best = best, Live = live,
                    Message = $"XCAN two-lens: target {p.Target:G4} reached after {passes} pass(es) ({label}).",
                };
        }

        return new XcanTwoLensResult
        {
            Success = true, Reached = false, Passes = passes, Last = last, Best = best, Live = live,
            Message = $"XCAN two-lens: target {p.Target:G4} NOT reached after {passes} pass(es) — last {live:G4} ({last}).",
        };
    }
}
