namespace Horizon.Station.UI;

/// <summary>
/// Per-head identity the shared windows read — set once by the head at startup, before the
/// first window opens (FAAM.UI: "FAAM"; DLAM.UI: "DLAM").
/// </summary>
public static class StationApp
{
    /// <summary>The head's executable name (entry assembly, e.g. "FAAM.UI") — the OEE session's app
    /// name and the Version window's product line.</summary>
    public static string AppName { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Horizon";

    /// <summary>Title of the shell, the shared dialogs / popups and the log source of shared code.</summary>
    public static string Title { get; set; } = "Horizon";

    /// <summary>The machine's full name — the startup screen's headline (FAAM: "Fiber Active Alignment Machine").</summary>
    public static string FullName { get; set; } = "Horizon Station";

    /// <summary>
    /// Creates the head's mascot (shell header, startup screen) — a <see cref="System.Windows.FrameworkElement"/>
    /// implementing <see cref="Views.IStationMascot"/>. Default: FAAM's <see cref="Views.PikaAvatar"/>.
    /// </summary>
    public static Func<Views.IStationMascot> Mascot { get; set; } = () => new Views.PikaAvatar();

    /// <summary>
    /// The default tool device — the motion device holding the gripper / tool (FAAM: "hex-right").
    /// Pre-fills the Tool device of the tool.* Engineer blocks and the Machine › Registration tool
    /// picker; both still offer every motion device. Set before the engine host is built: the Engineer
    /// catalog reads it once, on first use. Blank = no default.
    /// </summary>
    public static string ToolDevice { get; set; } = "";
}
