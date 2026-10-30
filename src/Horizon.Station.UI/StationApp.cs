namespace Horizon.Station.UI;

/// <summary>
/// Per-head identity the shared windows read — set once by the head at startup, before the
/// first window opens (FAAM.UI: "FAAM"; DLAM.UI: "DLAM").
/// </summary>
public static class StationApp
{
    /// <summary>Title of the shared dialogs / popups.</summary>
    public static string Title { get; set; } = "Horizon";

    /// <summary>
    /// The default tool device — the motion device holding the gripper / tool (FAAM: "hex-right").
    /// Pre-fills the Tool device of the tool.* Engineer blocks and the Machine › Registration tool
    /// picker; both still offer every motion device. Set before the engine host is built: the Engineer
    /// catalog reads it once, on first use. Blank = no default.
    /// </summary>
    public static string ToolDevice { get; set; } = "";
}
