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
    /// Station identity stamped onto DUT runs for traceability. Empty falls back to
    /// the Windows machine name at login. Machine-global (one machine = one station).
    /// </summary>
    public string StationId { get; init; } = string.Empty;

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

    // --- Camera ---

    /// <summary>
    /// When true, FAAM auto-starts the <c>main_camera</c> grab and pops up its live
    /// window as soon as the application has started (engine connected). Independent
    /// of any process-program run.
    /// </summary>
    public bool ShowMainCameraOnStartup { get; init; } = true;

    // --- Window persistence (restored on next launch) ---

    /// <summary>Main shell window placement, or null if never saved.</summary>
    public WindowPlacement? MainWindow { get; init; }

    /// <summary>Camera pop-out windows that were open at last exit — reopened on launch.</summary>
    public CameraWindowPlacement[] CameraWindows { get; init; } = Array.Empty<CameraWindowPlacement>();

    // --- Engineer page ---

    /// <summary>Name of the last loaded / saved process program — reloaded on launch. Empty = none.</summary>
    public string LastProgram { get; init; } = string.Empty;
}

/// <summary>Saved bounds + maximized state of a window.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>A saved camera pop-out window — which camera it showed and where.</summary>
public sealed record CameraWindowPlacement(string Camera, double Left, double Top, double Width, double Height);
