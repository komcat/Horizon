namespace DLAM.UI.ViewModels;

/// <summary>
/// DLAM's own Engineer blocks — the FireDog ports in Layer 2 (<c>StepDispatcher.FireDogPorts</c>,
/// docs/dlam-ui.md §5): XCAN, two-lens XCAN, climb, lift-off, map path and the two waits. Registered
/// into <see cref="OpCatalog"/> by <see cref="DlamHead"/>; the op / param keys mirror the dispatcher.
/// A blank tuning param means "the engine default" (the dispatcher only reads a param that is set),
/// so only the values an author normally changes carry a default here.
/// </summary>
public static class DlamOps
{
    public static readonly OpSpec[] All =
    {
        // ---- Scan ----
        // One XCAN pass on the block's hexapod: coordinate descent over a coarse → fine ladder.
        new("scan.xcan", "XCAN", "Scan", DeviceKind.Motion,
            Feedback().Concat(new[]
            {
                new ParamSpec("axes", "Axes (set; order is always Z, Y, X)", ParamKind.Text, "XYZ"),
                new ParamSpec("stepsUm", "Step ladder (µm, coarse → fine)", ParamKind.Text, "10,5,2,1"),
                new ParamSpec("exitTarget", "Exit target (0 = off; in unit)", ParamKind.Number, "0"),
                new ParamSpec("adaptive", "Adaptive ladder (signal-zoned)", ParamKind.Bool, "false"),
                new ParamSpec("zoneLow", "Zone low (in unit; = the hand-off level)", ParamKind.Number),
                new ParamSpec("zoneHigh", "Zone high (in unit)", ParamKind.Number),
                new ParamSpec("minSignal", "Refuse below this baseline (in unit, 0 = off)", ParamKind.Number, "0"),
                new ParamSpec("maxTravelUm", "Max travel per axis (µm, ≤ 1000)", ParamKind.Number, "200"),
                new ParamSpec("velocity", "Velocity (mm/s)", ParamKind.Number, "1"),
            }).Concat(Sampling()).Concat(new[]
            {
                new ParamSpec("avgCount", "Readings averaged per point", ParamKind.Number),
                new ParamSpec("consecLimit", "Consecutive worse steps before turning", ParamKind.Number),
                new ParamSpec("sigDropPct", "Signal drop that ends a walk (%)", ParamKind.Number),
                new ParamSpec("improvePct", "Min gain to count as better (%)", ParamKind.Number),
                new ParamSpec("skipOpposite", "Skip the opposite walk when the first gained", ParamKind.Bool, "true"),
                new ParamSpec("dirMemory", "Start each rung in the last good direction", ParamKind.Bool, "true"),
                new ParamSpec("reuseSample", "Reuse the reading at the current point", ParamKind.Bool, "true"),
                new ParamSpec("moveTimeoutMs", "Move timeout (ms)", ParamKind.Number),
                new ParamSpec("trace", "Log every sample (xcan,… CSV lines)", ParamKind.Bool, "false"),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "xcan"),
            }).ToArray()),

        // Up to four adaptive XCAN passes — left coarse, left coarse, left fine, right coarse —
        // ending as soon as one reaches the target.
        new("scan.xcanTwoLens", "XCAN Two Lens", "Scan", DeviceKind.None,
            new[]
            {
                new ParamSpec("left", "Left lens hexapod", ParamKind.MotionDeviceName, "hex-left"),
                new ParamSpec("right", "Right lens hexapod", ParamKind.MotionDeviceName, "hex-right"),
            }.Concat(Feedback()).Concat(new[]
            {
                new ParamSpec("target", "Target (in unit, required)", ParamKind.Number),
                new ParamSpec("zoneLow", "Zone low (in unit)", ParamKind.Number, "200"),
                new ParamSpec("zoneHigh", "Zone high (in unit, 0 = target)", ParamKind.Number, "0"),
                new ParamSpec("axes", "Axes (set; order is always Z, Y, X)", ParamKind.Text, "XYZ"),
                new ParamSpec("coarseStepsUm", "Coarse ladder (µm)", ParamKind.Text, "10,5,2,1"),
                new ParamSpec("fineStepsUm", "Fine ladder (µm)", ParamKind.Text, "2,1,0.5"),
                new ParamSpec("failIfMissed", "Fail the step when the target is not reached", ParamKind.Bool, "false"),
                new ParamSpec("maxTravelUm", "Max travel per axis (µm, ≤ 1000)", ParamKind.Number),
                new ParamSpec("velocity", "Velocity (mm/s)", ParamKind.Number),
            }).Concat(Sampling()).Concat(new[]
            {
                new ParamSpec("trace", "Log every sample", ParamKind.Bool, "false"),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "xtl"),
            }).ToArray()),

        // Step Z toward the part, search each XY plane for its maximum, stop on target / contact /
        // stall / Z budget; always parks on the best point.
        new("scan.climb", "Climb Scan", "Scan", DeviceKind.Motion,
            Feedback().Concat(new[]
            {
                new ParamSpec("target", "Target (in unit, required)", ParamKind.Number),
                new ParamSpec("targetPct", "Stop at % of target", ParamKind.Number, "85"),
                new ParamSpec("zAxis", "Approach axis", ParamKind.Text, "Z"),
                new ParamSpec("zDir", "Approach direction (+1 / -1)", ParamKind.Number, "1"),
                new ParamSpec("zStepUm", "Z step (µm)", ParamKind.Number, "1"),
                new ParamSpec("maxZUm", "Z budget (µm, ≤ 200)", ParamKind.Number, "20"),
                new ParamSpec("planeAxes", "Plane axes", ParamKind.Text, "X,Y"),
                new ParamSpec("planeScan", "Plane search", ParamKind.Choice, "cross", Choices: new[] { "cross", "spiral" }),
                new ParamSpec("xyStepUm", "Plane step (µm)", ParamKind.Number, "0.5"),
                new ParamSpec("xySteps", "Plane steps each way", ParamKind.Number, "6"),
                new ParamSpec("contactDropPct", "Contact: plane best under peak by (%)", ParamKind.Number, "10"),
                new ParamSpec("zProbePct", "Skip the plane when one Z step gains (%)", ParamKind.Number),
                new ParamSpec("maxSkips", "Max planes skipped in a row", ParamKind.Number),
                new ParamSpec("stallPlanes", "Stop after N planes without gain (0 = off)", ParamKind.Number),
                new ParamSpec("zSettleMs", "Z settle (ms)", ParamKind.Number),
                new ParamSpec("peakDrop", "Cross: readings past the peak", ParamKind.Number),
                new ParamSpec("crossPasses", "Cross: passes", ParamKind.Number),
                new ParamSpec("ringsStop", "Spiral: rings without gain", ParamKind.Number),
                new ParamSpec("improvePct", "Min gain to count as better (%)", ParamKind.Number),
                new ParamSpec("maxZSteps", "Max Z steps", ParamKind.Number),
                new ParamSpec("rasterStart", "Search the first plane before stepping Z", ParamKind.Bool, "false"),
                new ParamSpec("velocity", "Velocity (mm/s)", ParamKind.Number),
            }).Concat(Sampling()).Concat(new[]
            {
                new ParamSpec("avgCount", "Readings averaged per point", ParamKind.Number),
                new ParamSpec("moveTimeoutMs", "Move timeout (ms)", ParamKind.Number),
                new ParamSpec("trace", "Log every sample (clmb,… CSV lines)", ParamKind.Bool, "false"),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "climb"),
            }).ToArray()),

        // ---- Motion ----
        // Lift clear along one axis, then a normal Move to Position.
        new("motion.liftOff", "Lift Off & Move", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("position", "Then move to position", ParamKind.PositionName),
                new ParamSpec("liftMm", "Lift (mm, 0 = none, ≤ 50)", ParamKind.Number, "2"),
                new ParamSpec("liftAxis", "Lift axis (e.g. X+, Z-; blank = PI X+, ACS Z-)", ParamKind.Text),
            }.Concat(MoveOverrides(lighting: true)).ToArray()),

        // Leg by leg along the device's position map, over the fewest-hop route.
        new("motion.movePath", "Move Along Map", "Motion", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("to", "To position", ParamKind.PositionName),
                new ParamSpec("from", "Must start at (blank = whichever map node it is on)", ParamKind.PositionName),
                new ParamSpec("startTolMm", "Start tolerance (mm)", ParamKind.Number, "0.01"),
            }.Concat(MoveOverrides(lighting: false)).ToArray()),

        // ---- IO ----
        new("io.waitInput", "Wait for Input", "IO", DeviceKind.Io,
            new[]
            {
                new ParamSpec("pin", "Input pin (name or index)", ParamKind.Text),
                new ParamSpec("state", "Wait until ON", ParamKind.Bool, "true"),
                new ParamSpec("timeoutMs", "Timeout (ms, ≤ 600 000)", ParamKind.Number, "10000"),
                new ParamSpec("pollMs", "Poll every (ms)", ParamKind.Number, "20"),
            }),

        // ---- Flow ----
        new("flow.waitUntil", "Wait Until", "Flow", DeviceKind.None,
            new[]
            {
                new ParamSpec("signal", "Signal (or a variable below)", ParamKind.SignalName),
                new ParamSpec("var", "Variable (instead of a signal)", ParamKind.Text),
                new ParamSpec("op", "Condition", ParamKind.Choice, ">=", Choices: new[] { ">", ">=", "<", "<=", "==", "!=" }),
                new ParamSpec("value", "Value", ParamKind.Number, "0"),
                new ParamSpec("timeoutMs", "Timeout (ms, ≤ 3 600 000)", ParamKind.Number, "10000"),
                new ParamSpec("pollMs", "Poll every (ms)", ParamKind.Number, "50"),
            }),
    };

    /// <summary>Scan feedback: a catalog signal (read live) and the unit its levels are entered in.</summary>
    private static ParamSpec[] Feedback() => new[]
    {
        new ParamSpec("signal", "Feedback signal", ParamKind.SignalName),
        new ParamSpec("unit", "Level unit (uA / nA / mA / raw; blank = µA for a current)", ParamKind.Text),
    };

    /// <summary>The fresh-sample gate: wait for N new samples after each move, within the settle window.</summary>
    private static ParamSpec[] Sampling() => new[]
    {
        new ParamSpec("freshSamples", "Fresh samples after a move", ParamKind.Number),
        new ParamSpec("settleMs", "Settle ceiling (ms)", ParamKind.Number),
        new ParamSpec("settleMinMs", "Settle floor (ms)", ParamKind.Number),
    };

    /// <summary>The Move-to-Position overrides (velocity / approach / camera / exposure, optional lighting).</summary>
    private static IEnumerable<ParamSpec> MoveOverrides(bool lighting)
    {
        yield return new ParamSpec("velocity", "Velocity override (mm/s, 0 = use position)", ParamKind.Number, "0");
        yield return new ParamSpec("approachDistance", "Approach distance (mm before target, 0 = off)", ParamKind.Number, "0");
        yield return new ParamSpec("approachVelocity", "Approach velocity (mm/s, 0 = off)", ParamKind.Number, "0");
        yield return new ParamSpec("camera", "Camera override (blank = use position)", ParamKind.CameraName);
        yield return new ParamSpec("exposure", "Exposure override (µs, 0 = use position)", ParamKind.Number, "0");
        if (!lighting)
            yield break;
        yield return new ParamSpec("light", "Lighting (blank = leave as is)", ParamKind.LightName);
        yield return new ParamSpec("lightColor", "Light colour", ParamKind.LightColor, "white");
        yield return new ParamSpec("lightPercent", "Light intensity (%, 0 = off)", ParamKind.Number, "50");
    }
}
