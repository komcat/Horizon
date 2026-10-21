namespace Horizon.Common;

/// <summary>
/// Application-level settings — not hardware, so they live here in
/// <c>Horizon.Common</c> (shared by every front-end) rather than in the engine.
/// Persisted to <c>app_settings.json</c> in the config directory; loaded and
/// saved through <see cref="AppSettingsStore"/>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Camera scale: real-world millimetres per image pixel.</summary>
    public double MmPerPixel { get; init; } = 0.0025;

    /// <summary>
    /// Machine / station number stamped onto DUT runs for traceability (shown as
    /// "Machine number" in Settings). Empty falls back to the Windows machine name at
    /// login. Machine-global — one machine is one station.
    /// </summary>
    public string StationId { get; init; } = string.Empty;

    /// <summary>
    /// The machine's credential string — a fixed signature the station stamps onto every
    /// DUT run alongside <see cref="StationId"/>, so a unit's record says not just which
    /// machine ran it but under what standing (calibration certificate, asset tag,
    /// qualification id — whatever the line records). Free text, typed once in Settings
    /// and persisted; empty means the station is unsigned and runs are stamped without it.
    /// </summary>
    public string MachineCredential { get; init; } = string.Empty;

    /// <summary>
    /// When true (default), the app signs in automatically as the first active operator
    /// at startup, skipping the login prompt. Disable to require an explicit operator
    /// login (PIN) before the shell opens.
    /// </summary>
    public bool AutoLoginOperator { get; init; } = true;

    // --- MoveClick (FAAM Run page): click the live image to jog the stage by the
    //     real-world offset from the crosshair centre to the click point. ---

    /// <summary>Motion device the MoveClick jog commands. Empty disables the jog.</summary>
    public string MoveClickDevice { get; init; } = "gantry-main";

    /// <summary>Motion axis driven by the image's horizontal (X) direction.</summary>
    public string MoveClickXAxis { get; init; } = "X";

    /// <summary>Motion axis driven by the image's vertical (Y) direction.</summary>
    public string MoveClickYAxis { get; init; } = "Y";

    /// <summary>Sign (+1 / -1) applied to the X jog to match stage orientation.</summary>
    public double MoveClickXSign { get; init; } = 1.0;

    /// <summary>Sign (+1 / -1) applied to the Y jog to match stage orientation.</summary>
    public double MoveClickYSign { get; init; } = 1.0;

    // --- Jog velocity presets (mm/s), shown as the jog-panel preset buttons ---

    /// <summary>Jog velocity presets for hexapod-family devices (device id starts "hex").</summary>
    public double[] HexVelocityPresets { get; init; } = { 0.1, 0.5, 1, 2, 5, 10 };

    /// <summary>Jog velocity presets for ACS / gantry-family devices (everything else).</summary>
    public double[] GantryVelocityPresets { get; init; } = { 1, 5, 10, 20, 40, 75 };

    // --- Jog step presets (mm), shown as the jog-panel step quick-select buttons + scroll set ---

    /// <summary>Jog step-size presets (mm) for hexapod-family devices (device id starts "hex").</summary>
    public double[] HexStepPresetsMm { get; init; } =
        { 0.0005, 0.001, 0.002, 0.0025, 0.005, 0.01, 0.02, 0.025, 0.05, 0.1, 0.2, 0.25, 0.5, 1.0 };

    /// <summary>Jog step-size presets (mm) for ACS / gantry-family devices (everything else).</summary>
    public double[] GantryStepPresetsMm { get; init; } = { 0.1, 0.2, 0.25, 0.5, 1.0, 2.0, 5.0 };

    // --- FAU-attach process (Run-page UV cure dwells, seconds) ---

    /// <summary>UV step 1 cure time (seconds) — drives the 4.2 "UV step1" dwell after the UV_PLC1 pulse.</summary>
    public double UvStep1CuringSeconds { get; init; } = 5;

    /// <summary>UV step 2 / final cure time (seconds) — drives the 4.3 "UV step2" dwell after the UV_PLC2 pulse.</summary>
    public double UvCuringSeconds { get; init; } = 30;

    // --- FAU pre-align (Run-page protocol) target "master" offset ---

    /// <summary>Desired fiber→output offset distance (µm) the FAU pre-align corrects toward.</summary>
    public double FauPreAlignTargetUm { get; init; } = 100;

    /// <summary>Angle (degrees) of the FAU pre-align target offset vector.</summary>
    public double FauPreAlignTargetAngleDeg { get; init; } = 0;

    // --- Run-page alignment panels: collapse state of the reference-point group ---

    /// <summary>True if the U-correction panel's reference-point (Move/Save) group is collapsed.</summary>
    public bool UCorrectionRefPointsCollapsed { get; init; }

    /// <summary>True if the FAU pre-align panel's reference-point (Move/Save) group is collapsed.</summary>
    public bool FauPreAlignRefPointsCollapsed { get; init; }

    // --- Camera ---

    /// <summary>
    /// When true, FAAM auto-starts the <c>main_camera</c> grab and pops up its live
    /// window as soon as the application has started (engine connected). Independent
    /// of any process-program run.
    /// </summary>
    public bool ShowMainCameraOnStartup { get; init; } = true;

    /// <summary>
    /// Draw the live device-registration overlay (registered tool / gripped-object crosshair,
    /// outline box and label) on the production camera surfaces — the Run-page main feed, its
    /// Zoom2× inset and the camera pop-out. Default off (hidden). The teach / registration
    /// pages (Gripped FAU, Part Alignment, Machine › Registration) always draw it regardless.
    /// </summary>
    public bool ShowToolOverlay { get; init; }

    /// <summary>
    /// Exposure presets (µs) for the jog-panel Camera section's preset buttons.
    /// A zero entry is an unset (blank) button — the operator right-clicks it to
    /// type and save a value. Four entries back the four preset buttons.
    /// </summary>
    public double[] ExposurePresets { get; init; } = { 0, 0, 0, 0 };

    /// <summary>
    /// Settle wait (ms) after a vision profile pushes its saved exposure to the camera,
    /// before the frame used for detection is grabbed — lets the sensor finish applying
    /// the change so detection never runs on a mid-transition frame. 0 = no wait.
    /// </summary>
    public int VisionExposureSettleMs { get; init; } = 200;

    /// <summary>
    /// Per-point camera exposure (µs) applied before moving to a named reference
    /// point, keyed by the full position name (e.g. <c>FauPre_Out1</c>,
    /// <c>UCorr_P1</c>). Used by the U-correction and FAU pre-align panels' Move-to
    /// buttons and their Start-correction runs. 0 / absent = leave the exposure
    /// untouched (no setting applied before the move).
    /// </summary>
    public Dictionary<string, double> PointExposureUs { get; init; } = new();

    // --- Window persistence (restored on next launch) ---

    /// <summary>Main shell window placement, or null if never saved.</summary>
    public WindowPlacement? MainWindow { get; init; }

    /// <summary>Camera pop-out windows that were open at last exit — reopened on launch.</summary>
    public CameraWindowPlacement[] CameraWindows { get; init; } = Array.Empty<CameraWindowPlacement>();

    // --- Engineer page ---

    /// <summary>Name of the last loaded / saved process program — reloaded on launch. Empty = none.</summary>
    public string LastProgram { get; init; } = string.Empty;

    /// <summary>
    /// "Find First Light" post-FAU grid search tunables, edited on the Engineer page's
    /// Find First Light tab and mapped onto a <c>GridSearchConfig</c> when the Run-page
    /// alignment panel runs the search.
    /// </summary>
    public FindFirstLightSettings FindFirstLight { get; init; } = new();
}

/// <summary>
/// Operator-editable parameters for the post-FAU-pre-align "find first light" grid search
/// (the two-stage X-line → XY-grid signal finder). Mirrors the tunable fields of
/// <c>Horizon.Workflow.GridSearch.GridSearchConfig</c>; kept here in <c>Horizon.Common</c>
/// (which must not reference Layer 2) and mapped to a <c>GridSearchConfig</c> at run time.
/// Defaults match <c>GridSearchConfig</c>'s defaults.
/// </summary>
public sealed record FindFirstLightSettings
{
    /// <summary>Master switch — when false the whole find-first-light search is skipped after FAU pre-align.</summary>
    public bool Enabled { get; init; } = true;

    // --- Stage 1: X line search ---

    /// <summary>Run Stage 1 (the X line search).</summary>
    public bool Stage1Enabled { get; init; } = true;

    /// <summary>Initial +X excursion from centre (µm) before the downward sweep — the line "apex".</summary>
    public double LineJumpUm { get; init; } = 60;

    /// <summary>Step size for the −X sweep (µm).</summary>
    public double LineStepUm { get; init; } = 5;

    /// <summary>Number of −X steps after the apex.</summary>
    public int LineSteps { get; init; } = 24;

    /// <summary>After each −X step, sweep Y ±<see cref="LineYRangeUm"/> at <see cref="LineYStepUm"/> pitch.</summary>
    public bool LineYSweepEnabled { get; init; } = true;

    /// <summary>Half-width of the per-X-step Y sweep (µm).</summary>
    public double LineYRangeUm { get; init; } = 50;

    /// <summary>Y pitch of the per-X-step sweep (µm).</summary>
    public double LineYStepUm { get; init; } = 10;

    /// <summary>Stop the search as soon as any reading exceeds this absolute level (V); 0 = off.</summary>
    public double ExitAboveV { get; init; } = 0.1;

    // --- Stage 2: Y+ line-scan fallback ---

    /// <summary>Run Stage 2 (the Y+ line-scan fallback) when Stage 1 finds nothing.</summary>
    public bool Stage2Enabled { get; init; } = true;

    /// <summary>Number of Y+ rows to test; Stage 2 only steps Y in the + direction from centre.</summary>
    public int GridYRows { get; init; } = 12;

    /// <summary>Y+ pitch between rows (µm) — how far Y advances between successive X line scans.</summary>
    public double GridStepUm { get; init; } = 5;

    /// <summary>Hard cap on the total node count (runaway-sweep guard): GridYRows × (LineSteps + 1).</summary>
    public int MaxGridNodes { get; init; } = 4096;

    // --- Detection (shared by both stages) ---

    /// <summary>Absolute rise over baseline (V) that counts as "signal seen".</summary>
    public double DetectDeltaV { get; init; } = 0.02;

    /// <summary>Stage-1 early-exit drop, as a fraction of the peak's height above baseline (0..1).</summary>
    public double DropFraction { get; init; } = 0.3;

    /// <summary>Consecutive past-crest readings required to confirm the Stage-1 early exit.</summary>
    public int DropConfirmCount { get; init; } = 2;

    // --- Hand-off chain (after the grid search finds signal) ---

    /// <summary>Hand off to the hill climber once a grid stage finds signal. Default on.</summary>
    public bool HillClimbEnabled { get; init; } = true;

    /// <summary>Chain the Auto-W maximizer once after the hill climb parks at peak. Default off.</summary>
    public bool AutoWEnabled { get; init; }

    /// <summary>
    /// Settle wait (ms) after the hill climber switches the SiPhOG SLED to its operating current at
    /// the auto-SLED handoff, before re-baselining and continuing the climb. The SAG PWR voltage
    /// drops on the current change, so this lets it stabilise first.
    /// </summary>
    public int AutoSledWaitMs { get; init; } = 4000;

    // --- Automatic vision U-correction (runs before the U phase of Start correction) ---

    /// <summary>
    /// When on, "Start correction" first runs the vision-based U correction (measures the PIC/FAU
    /// facet line angles from their per-position vision profiles and rotates hex U by the delta),
    /// falling back to the manual P1/P2/F1/F2 capture flow if any facet isn't detected. Default off.
    /// </summary>
    public bool AutoVisionUCorrection { get; init; }

    /// <summary>
    /// Above this magnitude (deg) the auto vision U correction asks the operator to confirm before
    /// applying; at or below it, it applies silently.
    /// </summary>
    public double AutoVisionUConfirmDeg { get; init; } = 1.0;

    /// <summary>
    /// Above this magnitude (deg) the auto vision U correction is rejected as untrustworthy and the
    /// run falls back to the manual capture correction instead of applying.
    /// </summary>
    public double AutoVisionUMaxDeg { get; init; } = 3.0;
}

/// <summary>Saved bounds + maximized state of a window.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>A saved camera pop-out window — which camera it showed and where.</summary>
public sealed record CameraWindowPlacement(string Camera, double Left, double Top, double Width, double Height);
