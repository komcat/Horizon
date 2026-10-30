namespace DLAM.UI.ViewModels;

/// <summary>
/// Help for DLAM's own Engineer blocks (<see cref="DlamOps"/>) — registered into
/// <see cref="OpCatalogHelp"/> by the head. Same shape and authoring guide as the generic help
/// (<c>docs/engineer-command-help.md</c>); the algorithms are described in docs/dlam-ui.md §5.
/// </summary>
public static class DlamOpHelp
{
    // Declared before ByKey — static fields initialize in order.
    private static readonly (string Key, string Note)[] FeedbackNotes =
    {
        ("signal", "The photocurrent to maximise, read live from the catalog — e.g. smu.Keithley_2602B.ch1.current. Bias the SMU and switch its output on first (smu.setSourceVoltage + smu.outputOn)."),
        ("unit", "Unit of every level on this block (target, zones, exit, min signal): uA, nA, mA or raw. Blank = µA for a current signal (FireDog's convention), raw otherwise. Results are written in amps."),
    };

    private static readonly (string Key, string Note)[] SamplingNotes =
    {
        ("freshSamples", "After each move, wait for this many NEW readings before measuring (2 = the second is entirely after the move). Blank = 2."),
        ("settleMs", "Longest wait for those readings (ms). Blank = 300."),
        ("settleMinMs", "Always wait at least this long after a move (ms). Blank = 50 — kept until PI moves wait for on-target."),
    };

    private static readonly (string Key, string Note)[] MoveNotes =
    {
        ("velocity", "Travel velocity (mm/s) for the move; 0 = the position's own velocity."),
        ("approachDistance", "Finish the last N mm slowly; 0 = no approach. Needs an approach velocity too."),
        ("approachVelocity", "Velocity for the approach (mm/s); 0 = no approach."),
        ("camera", "Camera to switch to during the move; blank = the position's camera."),
        ("exposure", "Exposure (µs) applied at the start of the move; 0 = the position's exposure."),
    };

    public static readonly IReadOnlyDictionary<string, OpHelp> ByKey = new Dictionary<string, OpHelp>(StringComparer.OrdinalIgnoreCase)
    {
        // ===== Scan =====

        ["scan.xcan"] = new()
        {
            Summary = "One XCAN pass on the block's hexapod — climb the photocurrent axis by axis over a coarse → fine step ladder.",
            Usage = "FireDog's XCAN. For each step size (e.g. 10, 5, 2, 1 µm) and each axis in the set (always in the order Z, Y, X), it walks one way while the signal rises, tries the other way unless the peak is already bracketed, keeps the better point, and returns to the best point seen so far before the next axis.\n\nExit target: the pass stops on the first point at or above it and stays there. Adaptive: the ladder starts at a step size chosen from the signal level (Zone low … Zone high, equal-log zones), so a lens already near peak skips the coarse steps.\n\nOn STOP, a motion failure or an error the hexapod returns to the best point measured. Results: {captureAs}_baseline, _best, _peak, _delta, _gain, _exitHit, _passes (in amps).",
            ParamNotes = FeedbackNotes.Concat(new[]
            {
                ("axes", "Which axes to scan (e.g. XYZ, XY). The order is always Z, Y, X."),
                ("stepsUm", "Step ladder in µm, coarse to fine, comma-separated. Each step ≤ 100 µm."),
                ("exitTarget", "Stop as soon as a reading reaches this level (in unit) and stay there. 0 = off."),
                ("adaptive", "Pick the starting rung from the signal level (Zone low … Zone high)."),
                ("zoneLow", "Signal at or below it uses the coarsest step. Must equal the level the previous stage hands over at. Blank = 1."),
                ("zoneHigh", "Signal at or above it uses the finest step. Blank = 400."),
                ("minSignal", "Refuse to scan when the starting reading is below this (in unit) — e.g. the light is off. 0 = off."),
                ("maxTravelUm", "Furthest the pass may move each axis from its start (µm, ≤ 1000)."),
                ("velocity", "Move velocity (mm/s)."),
            }).Concat(SamplingNotes).Concat(new[]
            {
                ("avgCount", "Readings averaged at each point. Blank = 3."),
                ("consecLimit", "Worse points in a row before a walk turns back. Blank = 3."),
                ("sigDropPct", "A walk also ends when the signal falls this far below the best (%). Blank = 15."),
                ("improvePct", "A point counts as better only above this gain (%). Blank = 1."),
                ("skipOpposite", "Skip the opposite walk when the first direction gained."),
                ("dirMemory", "Start each rung in the direction that worked last time."),
                ("reuseSample", "Reuse the reading already taken at the current point instead of measuring again."),
                ("moveTimeoutMs", "Longest a single move may take (ms). Blank = 10 000."),
                ("trace", "Log every sample as an xcan,… CSV line."),
                ("captureAs", "Prefix of the result variables."),
            }).ToArray(),
        },

        ["scan.xcanTwoLens"] = new()
        {
            Summary = "Align both lenses: up to four adaptive XCAN passes — left coarse, left coarse, left fine, right coarse — stopping as soon as the target is reached.",
            Usage = "FireDog's xcan_two_lens. Every pass exits at the target; the sequence ends the moment one pass reaches it (its exit fired, or a fresh reading is at or above it). The step ladders and the other pass settings apply to every pass.\n\nThe step succeeds whether or not the target was reached, unless 'Fail the step when the target is not reached' is on. Results: {captureAs}_reached, _passes, _last, _best, _live.",
            ParamNotes = new[]
            {
                ("left", "Hexapod holding the left lens (scanned first, three times)."),
                ("right", "Hexapod holding the right lens (one coarse pass at the end)."),
            }.Concat(FeedbackNotes).Concat(new[]
            {
                ("target", "Level to reach (in unit) — required. FireDog production: 6800 µA."),
                ("zoneLow", "Adaptive zone low (in unit) — the coarsest-step level. Default 200."),
                ("zoneHigh", "Adaptive zone high (in unit); 0 = the target."),
                ("axes", "Which axes each pass scans (order always Z, Y, X)."),
                ("coarseStepsUm", "Ladder for the coarse passes (µm)."),
                ("fineStepsUm", "Ladder for the fine pass (µm)."),
                ("failIfMissed", "Fail the step when no pass reaches the target."),
                ("maxTravelUm", "Furthest each pass may move an axis (µm). Blank = 200."),
                ("velocity", "Move velocity (mm/s). Blank = 1."),
            }).Concat(SamplingNotes).Concat(new[]
            {
                ("trace", "Log every sample."),
                ("captureAs", "Prefix of the result variables."),
            }).ToArray(),
        },

        ["scan.climb"] = new()
        {
            Summary = "Close the gap: step along Z toward the part, search each XY plane for its maximum, and stop on target, contact, stall or the Z budget.",
            Usage = "FireDog's climb raster. Each Z step is probed in place; while one Z step alone gains more than the skip threshold the plane search is skipped. Otherwise the XY plane is searched (cross line scans or a spiral) and the lens moves to its maximum.\n\nIt stops when the signal reaches Target × Stop at %, when a plane's best falls clearly under the peak (suspected contact), when planes stop gaining, or when the Z budget is used — and always parks on the best point of the run (retreating Z first, advancing Z last).\n\nResults: {captureAs}_baseline, _best, _final, _target, _targetFrac, _stop, _exitHit, _zUm, _bestZUm, _bestAUm, _bestBUm, _planes, _skipped, _points, _gain.",
            ParamNotes = FeedbackNotes.Concat(new[]
            {
                ("target", "Expected peak signal (in unit) — required."),
                ("targetPct", "Stop once the signal reaches this % of the target."),
                ("zAxis", "The approach axis."),
                ("zDir", "+1 or -1: the direction that closes the gap."),
                ("zStepUm", "Z step (µm)."),
                ("maxZUm", "Total Z travel allowed (µm, ≤ 200)."),
                ("planeAxes", "The two plane axes (e.g. X,Y)."),
                ("planeScan", "cross = line scans through the current point; spiral = rings outward."),
                ("xyStepUm", "Plane step (µm)."),
                ("xySteps", "Plane steps each way from the current point."),
                ("contactDropPct", "A plane whose best is this far (%) under the run's peak counts as contact — stop."),
                ("zProbePct", "Skip the plane search while one Z step gains more than this (%). Blank = 5."),
                ("maxSkips", "Most planes skipped in a row. Blank = 5."),
                ("stallPlanes", "Stop after this many planes without a gain. 0 / blank = off."),
                ("zSettleMs", "Settle after a Z step (ms). Blank = 200."),
                ("peakDrop", "Cross scan: readings past the peak before a line ends. Blank = 2."),
                ("crossPasses", "Cross scan: passes per plane. Blank = 1."),
                ("ringsStop", "Spiral: rings without a gain before it stops. Blank = 2."),
                ("improvePct", "A point counts as better only above this gain (%). Blank = 2."),
                ("maxZSteps", "Most Z steps. Blank = 2000."),
                ("rasterStart", "Search the first plane before the first Z step."),
                ("velocity", "Move velocity (mm/s)."),
            }).Concat(SamplingNotes).Concat(new[]
            {
                ("avgCount", "Readings averaged at each point."),
                ("moveTimeoutMs", "Longest a single move may take (ms)."),
                ("trace", "Log every sample as a clmb,… CSV line."),
                ("captureAs", "Prefix of the result variables."),
            }).ToArray(),
        },

        // ===== Motion =====

        ["motion.liftOff"] = new()
        {
            Summary = "Lift the device clear along one axis, then move to a position.",
            Usage = "Lifts Lift mm along the lift axis at the current velocity (e.g. to pull a lens or a needle away from the part), then does a normal Move to Position with the overrides below. Everything is checked before anything moves: the position must be taught and the axis must exist.",
            ParamNotes = new[]
            {
                ("position", "Where to go after the lift (taught on this device)."),
                ("liftMm", "Lift distance (mm, ≤ 50). 0 = no lift — just the move."),
                ("liftAxis", "Axis and direction, e.g. X+ or Z-. Blank = X+ on a PI hexapod, Z- on an ACS stage."),
            }.Concat(MoveNotes).Concat(new[]
            {
                ("light", "Lighting call-out applied during the move; blank = leave lighting as is."),
                ("lightColor", "Colour of that call-out."),
                ("lightPercent", "Intensity (%) of that call-out; 0 = off."),
            }).ToArray(),
        },

        ["motion.movePath"] = new()
        {
            Summary = "Move to a position leg by leg along the device's position map, over the route with the fewest hops.",
            Usage = "Uses the links drawn on Machine › Motion › position map for this device. The device must already stand on a map node (or on 'Must start at', within the start tolerance) — it is never moved to the start implicitly. Arrival is confirmed after every leg. An approach, if set, is used on the last leg only.",
            ParamNotes = new[]
            {
                ("to", "Destination node (a taught position on the map)."),
                ("from", "The node the device must be standing on. Blank = whichever node it stands on."),
                ("startTolMm", "How close (mm) the device must be to the start node."),
            }.Concat(MoveNotes).ToArray(),
        },

        // ===== IO =====

        ["io.waitInput"] = new()
        {
            Summary = "Wait until an input pin of the block's IO device reads ON (or OFF).",
            Usage = "Polls the IO device's input state. Fails after the timeout. Use it to wait for a part-present sensor, a door, a foot switch …",
            ParamNotes = new[]
            {
                ("pin", "Input pin name (as configured) or index."),
                ("state", "On = wait for ON; off = wait for OFF."),
                ("timeoutMs", "Fail after this long (ms, ≤ 600 000)."),
                ("pollMs", "How often the input is read (ms)."),
            },
        },

        // ===== Flow =====

        ["flow.waitUntil"] = new()
        {
            Summary = "Wait until a signal or a program variable meets a condition.",
            Usage = "Set exactly one of Signal (read live from the catalog) or Variable (e.g. set by another parallel lane). The value is compared with the condition every poll; the step fails after the timeout.",
            ParamNotes = new[]
            {
                ("signal", "Catalog signal to read (e.g. smu.Keithley_2602B.ch1.current, in its own unit)."),
                ("var", "Program variable to watch instead of a signal."),
                ("op", "Comparison: the reading on the left, Value on the right."),
                ("value", "The value to compare with."),
                ("timeoutMs", "Fail after this long (ms, ≤ 3 600 000)."),
                ("pollMs", "How often to check (ms)."),
            },
        },
    };
}
