namespace Horizon.Station.UI;

/// <summary>
/// Per-head identity the shared windows read — set once by the head at startup, before the
/// first window opens (FAAM.UI: "FAAM"; DLAM.UI: "DLAM").
/// </summary>
public static class StationApp
{
    /// <summary>Title of the shared dialogs / popups.</summary>
    public static string Title { get; set; } = "Horizon";
}
