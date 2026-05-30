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
}
