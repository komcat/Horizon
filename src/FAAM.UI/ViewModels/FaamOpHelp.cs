namespace FAAM.UI.ViewModels;

/// <summary>
/// Help for FAAM's own Engineer blocks (<see cref="FaamOps"/>) — registered into
/// <see cref="OpCatalogHelp"/> by the head. Same shape and authoring guide as the generic help
/// (<c>docs/engineer-command-help.md</c>).
/// </summary>
public static class FaamOpHelp
{
    // Shared by align.correction and the two single-phase blocks. Declared before ByKey —
    // static fields initialize in order, so ByKey would otherwise see null here.
    private static readonly (string Key, string Note)[] UCorrectionNotes =
    {
        ("uP1", "First point on the PIC line (gantry-main position). Default UCorr_P1."),
        ("uP2", "Second point on the PIC line. Default UCorr_P2."),
        ("uF1", "First point on the FAU line. Default UCorr_F1."),
        ("uF2", "Second point on the FAU line. Default UCorr_F2."),
        ("maxUDeg", "Largest U rotation (°) that will be applied; above it the step fails without moving. Also caps Vision U, which then falls back to manual capture."),
    };

    private static readonly (string Key, string Note)[] PlaceFauNotes =
    {
        ("out1", "First PIC output point (gantry-main position). Default FauPre_Out1."),
        ("out2", "Second PIC output point. Default FauPre_Out2."),
        ("fiber1", "First FAU fiber point. Default FauPre_Fiber1."),
        ("fiber2", "Second FAU fiber point. Default FauPre_Fiber2."),
        ("gapUm", "Target distance from the PIC output to the FAU fiber (µm). Blank = the target set on the Run-page panel."),
        ("angleDeg", "Direction of that target offset (°). Blank = the Run-page panel setting."),
        ("waveguideDeg", "Waveguide tilt (°). After the move, hex Y goes up by gap × tan(angle). 0 = no extra move."),
        ("maxCorrectionMm", "Longest total FAU move (mm), including the waveguide follow-up. Above it the step fails without moving."),
    };

    public static readonly IReadOnlyDictionary<string, OpHelp> ByKey = new Dictionary<string, OpHelp>(StringComparer.OrdinalIgnoreCase)
    {
        // ===== Align =====

        ["align.correction"] = new()
        {
            Summary = "Run the Run-page \"Start correction\" (U correction → accuracy place FAU) and wait until it finishes.",
            Usage = "Same routine as the button on the U CORR → ACCURACY PLACE FAU panel: optional vision U, then the gantry-main reference points (all must be taught). At each point the operator refines the location by clicking the live view (MoveClick) and presses Capture (on the panel or the camera feed); the correction is then applied automatically, followed by the grid search if Find First Light is on.\n\nThe points, gap, angle and limits are the same as on the U Correction and Accuracy Place FAU blocks — use those to run one half on its own.\n\nUnlike \"Open & Run App\", the sequence WAITS here. A Cancel on the prompt, the panel's Stop, or a failed phase fails the step; a program Stop aborts the correction. FAAM.UI only.",
            ParamNotes = new[]
            {
                ("runU", "Run the U-correction phase (angle on hex U)."),
                ("runFau", "Run the accuracy place FAU phase (hex Y/Z move). If U fails, FAU is skipped and the step fails."),
            }.Concat(UCorrectionNotes).Concat(PlaceFauNotes).ToArray(),
        },

        ["align.uCorrection"] = new()
        {
            Summary = "U correction on its own: measure the PIC line vs the FAU line and rotate hex U by the difference.",
            Usage = "The gantry visits the four points in order (PIC 1 → PIC 2 → FAU 1 → FAU 2). At each, the operator centers the crosshair (MoveClick) and presses Capture on the Run page. The angle between the PIC line and the FAU line is then applied as a slow relative U move on hex-right. If Vision U is on (Find First Light), it is tried first and the manual capture is the fallback.\n\nThe step waits until it finishes. Untaught or repeated points, a U larger than the limit, a Cancel or a program Stop fail the step. FAAM.UI only.",
            ParamNotes = UCorrectionNotes,
        },

        ["align.placeFau"] = new()
        {
            Summary = "Accuracy place FAU (formerly FAU pre-align) on its own: move the FAU to the set gap and angle from the PIC output.",
            Usage = "The gantry visits Output 1 → Output 2 → Fiber 1 → Fiber 2. At each, the operator centers the crosshair (MoveClick) and presses Capture on the Run page. The routine measures where the fiber is relative to the output, works out the move to the target (gap at the target angle), and applies it on hex-right Y/Z at slow speed. It then raises hex Y by gap × tan(waveguide angle) so the FAU follows the tilted waveguide.\n\nIf the total move would be longer than 'Max FAU move', nothing moves and the step fails — re-check the captured points. Untaught or repeated points, a Cancel or a program Stop also fail the step. FAAM.UI only.",
            ParamNotes = PlaceFauNotes,
        },

        ["fau.gridScan"] = new()
        {
            Summary = "Find first light: scan hex-right over a line + sweep grid and climb onto the signal as soon as it shows (runs FAU_GridScan).",
            Usage = "Start after Accuracy Place FAU, with the SiPhOG streaming. The line axis steps from +range to −range; at each step the 2nd axis is swept ±sweep range. Z never moves.\n\nSignal is judged against this unit's own noise floor, not a fixed voltage: the floor is the median of the readings so far (the dark level, which may be negative), and a reading counts when it is more than max(K × the start noise, min) above it. The point is measured again before it counts. If the FAU already sees light at the start, the floor drops as soon as the scan reaches the dark and the climb starts from the start point.\n\nAt the end the FAU is parked on the best point — follow with FAU Power Climb. Runs the product's FAU_GridScan script; its log lines (one per point, '^ signal' marks) go to the log.",
            ParamNotes = new[]
            {
                ("axes", "X,Y = line along X, sweep along Y (default). Y,X swaps them."),
                ("lineRangeUm", "The line runs from +this to −this (µm) about the start."),
                ("lineStepUm", "Line step (µm)."),
                ("sweep", "Sweep the 2nd axis at every line step. Off = a single line."),
                ("sweepRangeUm", "The sweep covers −this … +this (µm) at each line step."),
                ("sweepStepUm", "Sweep pitch (µm)."),
                ("climb", "Stop the grid at the first confirmed signal and climb onto its peak (1 µm steps). Off = finish the grid and park on the best point."),
                ("noiseK", "Signal = floor + K × the start noise (std of the T0 frames). Higher = fewer false starts, less sensitive."),
                ("minDeltaV", "Minimum rise over the floor (V), for a unit whose start noise is tiny."),
                ("moreArgs", "Any other FAU_GridScan key, e.g. { samples = 5, yRows = 12 }. Wins over the fields."),
            },
        },

        ["fau.powerClimb"] = new()
        {
            Summary = "Climb SAG PWR coarse-to-fine on hex-right (Z+ with Y re-centring, then Y and X) and finish on the best point (runs FAU_PowerClimb).",
            Usage = "Start where the FAU already couples some light (e.g. after FAU Grid Scan). Each mode is a set of {Z, XY} steps: coarse 10→0.5 µm Z, fine 2→0.5 µm Z, peak XY = X/Y only (no Z). Z never advances more than 40 µm toward the PIC.\n\nExit: the climb stops early once SAG beats the target. Blank threshold = the target from the photo-current table (300/500/800 µA → 1.1/1.5/2.0 V); a number = that SAG level whatever the photo current. Untick Exit to climb every step set.",
            ParamNotes = new[]
            {
                ("mode", "coarse, fine or peak XY."),
                ("exit", "Stop early once SAG beats the target. Off = climb every step set."),
                ("exitV", "Fixed exit level (V). Blank = the photo-current table."),
                ("moreArgs", "Any other FAU_PowerClimb key, e.g. { zBudgetUm = 30 } or { sets = {{1,0.5},{0.5,0.25}} }. Wins over the fields."),
            },
        },

        ["fau.wOptimize"] = new()
        {
            Summary = "Optimise FAU roll (hex-right W), re-centring X/Y after every W step, and park on the best pose (runs FAU_WOptimize).",
            Usage = "Start with the FAU coupled (after FAU Power Climb). Probes W+ and W− from the start, then keeps stepping the better direction while SAG rises. X/Y are re-centred after each W move, so a step is judged on the centred reading. Z never moves.",
            ParamNotes = new[]
            {
                ("wStep", "W step (deg)."),
                ("wLimitDeg", "Never move W more than this from the start (deg)."),
                ("maxWSteps", "Max W steps in the winning direction."),
                ("xySets", "X/Y centring steps after each W move (µm), coarse to fine, e.g. 1, 0.5."),
                ("minGainPct", "A step must beat the best by this % (or 3× the start noise) to count as a rise."),
                ("moreArgs", "Any other FAU_WOptimize key, e.g. { centreStart = false }. Wins over the fields."),
            },
        },

        ["siphog.saveSnapshot"] = new()
        {
            Summary = "Save a labelled snapshot of the six SiPhOG readings (SLED current, photo current, SLED temp, target SAG PWR, SAG PWR, TEC current) for this DUT.",
            Usage = "Drop it at each point you want on record (e.g. \"wet\" after epoxy, \"uv1.0\" after the first cure, \"final\"). Each value is averaged over the window and stored with its min / max / std, the time, the operator and this block's name. Every snapshot is appended to the product folder's siphog_data\\<serial>.json, one file per DUT, so the sets stack up in order and can be compared on the DUT Data page. Re-using a label adds a new set; the newest one with that label is the one shown.\n\nNeeds a DUT serial (a \"DUT Entry\" step or an up-front serial) and the Run-page SiPhOG client connected. If either is missing the block fails softly: with \"Ask operator if it fails\" on (default) a dialog asks Continue or Stop; off, it logs a warning and the run carries on.",
            ParamNotes = new[]
            {
                ("label", "Name of this set, e.g. wet, uv1.0, final. Use var:name to take it from a context variable."),
                ("windowMs", "How long to sample and average, in ms (SiPhOG streams about 200 Hz). 0 takes one frame. Maximum 10000."),
                ("note", "Free text stored with the set (optional)."),
                ("askOnFailure", "On: a failure pops up Continue / Stop. Off: the block is skipped with a warning in the log and the run carries on."),
            },
        },
    };
}
