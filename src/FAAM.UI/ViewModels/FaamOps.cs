namespace FAAM.UI.ViewModels;

/// <summary>
/// FAAM's own Engineer blocks — the FAU alignment group (hill climb, U → FAU correction, the
/// FAU script front-ends) and the SiPhOG blocks — registered into <see cref="OpCatalog"/> by the
/// head (<see cref="FaamHead"/>). The op / param keys mirror <c>StepDispatcher</c>.
/// </summary>
public static class FaamOps
{
    public static readonly OpSpec[] All =
    {
        // ---- Align (Hill Climber) ----
        // Anchors / clears the lifetime Z+ budget on the chosen motion device (hex). Once an
        // anchor is set, each Start Hill Climb only gets the travel earlier climbs didn't use,
        // so re-running can't silently spend the budget twice.
        new("hillclimb.setAnchor", "Set Hill Anchor", "Align", DeviceKind.Motion, System.Array.Empty<ParamSpec>()),
        new("hillclimb.clearAnchor", "Clear Hill Anchor", "Align", DeviceKind.Motion, System.Array.Empty<ParamSpec>()),
        // Runs the SiPhOG-fed hill climb to completion on the device. 'distance' is the lifetime
        // ceiling (µm); with an anchor set the run uses what's left of it. 'targetV' > 0 exits
        // early at ≥90% of target. Results land in '{captureAs}_vpeak/_vstart/_ztraveled/_stop'.
        new("hillclimb.start", "Start Hill Climb", "Align", DeviceKind.Motion,
            new[]
            {
                new ParamSpec("distance", "Distance / ceiling (µm)", ParamKind.Number, "98"),
                new ParamSpec("targetV", "Target Sagnac V (0 = disabled)", ParamKind.Number, "0"),
                new ParamSpec("captureAs", "Capture prefix", ParamKind.Text, "hillclimb"),
            }),
        // Aborts a climb started in a parallel lane (Start blocks its own lane). No-op when idle.
        new("hillclimb.stop", "Stop Hill Climb", "Align", DeviceKind.None, System.Array.Empty<ParamSpec>()),
        // The Run-page "Start correction" (U correction → accuracy place FAU), awaited to completion.
        // The operator confirms each gantry-main point on the Run page (Capture; MoveClick between).
        // The points / gap / angle / guards below are shared with the two single-phase blocks.
        new("align.correction", "U → FAU Correction", "Align", DeviceKind.None,
            new[]
            {
                new ParamSpec("runU", "Run U correction", ParamKind.Bool, "true"),
                new ParamSpec("runFau", "Run accuracy place FAU", ParamKind.Bool, "true"),
            }.Concat(UCorrectionParams()).Concat(PlaceFauParams()).ToArray()),
        // U correction alone: PIC line (P1→P2) vs FAU line (F1→F2) → relative hex U.
        new("align.uCorrection", "U Correction", "Align", DeviceKind.None, UCorrectionParams()),
        // Accuracy place FAU alone (the FAU pre-align): Out1/Out2 vs Fiber1/Fiber2 → hex Y/Z move
        // to the gap/angle target, then the waveguide-angle Y+ follow-up.
        new("align.placeFau", "Accuracy Place FAU", "Align", DeviceKind.None, PlaceFauParams()),

        // FAU alignment blocks — typed front-ends for the product's FAU_GridScan / FAU_PowerClimb /
        // FAU_WOptimize scripts (Layer 2 FauScriptBlocks maps each field to a script argument; blank =
        // the script default). 'moreArgs' passes any other script key (Lua table text, wins last).
        new("fau.gridScan", "FAU Grid Scan", "Align", DeviceKind.None,
            new[]
            {
                new ParamSpec("axes", "Axes (line, sweep)", ParamKind.Choice, "X,Y", Choices: new[] { "X,Y", "Y,X" }),
                new ParamSpec("lineRangeUm", "Line range ± (µm)", ParamKind.Number, "50"),
                new ParamSpec("lineStepUm", "Line step (µm)", ParamKind.Number, "10"),
                new ParamSpec("sweep", "Sweep the 2nd axis at every line step", ParamKind.Bool, "true"),
                new ParamSpec("sweepRangeUm", "Sweep range ± (µm)", ParamKind.Number, "50"),
                new ParamSpec("sweepStepUm", "Sweep step (µm)", ParamKind.Number, "5"),
                new ParamSpec("climb", "Climb at the first signal", ParamKind.Bool, "true"),
                new ParamSpec("noiseK", "Signal = noise floor + K × noise (K)", ParamKind.Number, "6"),
                new ParamSpec("minDeltaV", "… but at least (V) over the floor", ParamKind.Number, "0.005"),
                new ParamSpec("moreArgs", "More arguments (Lua, optional)", ParamKind.Text),
            }),
        new("fau.powerClimb", "FAU Power Climb", "Align", DeviceKind.None,
            new[]
            {
                new ParamSpec("mode", "Mode", ParamKind.Choice, "coarse", Choices: new[] { "coarse", "fine", "peak XY" }),
                new ParamSpec("exit", "Exit early at the SAG target", ParamKind.Bool, "true"),
                new ParamSpec("exitV", "Exit threshold SAG (V, blank = photo-current table)", ParamKind.Number),
                new ParamSpec("moreArgs", "More arguments (Lua, optional)", ParamKind.Text),
            }),
        new("fau.wOptimize", "FAU W Optimize", "Align", DeviceKind.None,
            new[]
            {
                new ParamSpec("wStep", "W step (deg)", ParamKind.Number, "0.15"),
                new ParamSpec("wLimitDeg", "Max W change (deg)", ParamKind.Number, "1.5"),
                new ParamSpec("maxWSteps", "Max W steps", ParamKind.Number, "10"),
                new ParamSpec("xySets", "XY centring steps (µm, coarse to fine)", ParamKind.Text, "1, 0.5"),
                new ParamSpec("minGainPct", "Min gain to count as a rise (%)", ParamKind.Number, "0.5"),
                new ParamSpec("moreArgs", "More arguments (Lua, optional)", ParamKind.Text),
            }),

        // ---- SiPhOG -> Google Sheet (V11 UPDATE_SAG) ----
        // Writes the bench row's current SAG-PWR readings into the chosen stage column of
        // the SiPhOG server's Google Sheet. The bench (column-A key) is the run's DUT serial
        // — taken automatically, so there's no field for it; start a DUT run / DUT Entry first.
        // The measured values come from the SiPhOG server's own live stream, so only the stage
        // and completed flag are sent. The step blocks until the server's ACK/ERR (a Google
        // round-trip) and fails the program on ERR. Needs the Run-page SiPhOG client connected.
        new("siphog.updateSheet", "Save SAG to Sheet", "Signal", DeviceKind.None,
            new[]
            {
                new ParamSpec("stage", "Stage (ACTUAL / WET / UV)", ParamKind.Text, "ACTUAL"),
                new ParamSpec("completed", "Mark stage completed", ParamKind.Bool, "true"),
                new ParamSpec("askOnFailure", "Ask operator if it fails (else skip silently)", ParamKind.Bool, "true"),
            }),

        // Freezes the six SiPhOG panel values (averaged over windowMs, with min/max/std) for the
        // run's DUT serial, tagged with a label, and appends the set to the product's
        // siphog_data\{serial}.json. Best-effort: a failure is a warning and the run continues.
        new("siphog.saveSnapshot", "Save SiPhOG Snapshot", "Signal", DeviceKind.None,
            new[]
            {
                new ParamSpec("label", "Label (e.g. wet, uv1.0, final)", ParamKind.Text, "snapshot"),
                new ParamSpec("windowMs", "Average over (ms, 0 = single frame)", ParamKind.Number, "500"),
                new ParamSpec("note", "Note (optional)", ParamKind.Text, ""),
                new ParamSpec("askOnFailure", "Ask operator if it fails (else skip silently)", ParamKind.Bool, "true"),
            }),
    };

    // Methods (not fields) so they're safe to call from the All initializer above.
    // The align.* reference points live on gantry-main — the pickers list its positions.
    private const string AlignGantry = "gantry-main";

    /// <summary>U-correction params: the four gantry points (defaults = the panel's UCorr_* names) + the U guard.</summary>
    private static ParamSpec[] UCorrectionParams() => new[]
    {
        new ParamSpec("uP1", "U · PIC point 1", ParamKind.PositionName, "UCorr_P1", AlignGantry),
        new ParamSpec("uP2", "U · PIC point 2", ParamKind.PositionName, "UCorr_P2", AlignGantry),
        new ParamSpec("uF1", "U · FAU point 1", ParamKind.PositionName, "UCorr_F1", AlignGantry),
        new ParamSpec("uF2", "U · FAU point 2", ParamKind.PositionName, "UCorr_F2", AlignGantry),
        new ParamSpec("maxUDeg", "Max U correction (°)", ParamKind.Number, "2"),
    };

    /// <summary>Accuracy-place-FAU params: the four gantry points, the gap/angle target, waveguide angle and the move guard.</summary>
    private static ParamSpec[] PlaceFauParams() => new[]
    {
        new ParamSpec("out1", "FAU · Output point 1", ParamKind.PositionName, "FauPre_Out1", AlignGantry),
        new ParamSpec("out2", "FAU · Output point 2", ParamKind.PositionName, "FauPre_Out2", AlignGantry),
        new ParamSpec("fiber1", "FAU · Fiber point 1", ParamKind.PositionName, "FauPre_Fiber1", AlignGantry),
        new ParamSpec("fiber2", "FAU · Fiber point 2", ParamKind.PositionName, "FauPre_Fiber2", AlignGantry),
        new ParamSpec("gapUm", "PIC↔FAU gap (µm, blank = Run-page setting)", ParamKind.Number),
        new ParamSpec("angleDeg", "Target angle (°, blank = Run-page setting)", ParamKind.Number),
        new ParamSpec("waveguideDeg", "Waveguide angle (°, 0 = no Y follow-up)", ParamKind.Number, "7"),
        new ParamSpec("maxCorrectionMm", "Max FAU move (mm)", ParamKind.Number, "0.5"),
    };
}
