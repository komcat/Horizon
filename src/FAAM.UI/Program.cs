using Velopack;

namespace FAAM.UI;

/// <summary>
/// Process entry point (<c>StartupObject</c> in FAAM.UI.csproj). Runs Velopack's hook
/// handler before WPF starts, then hands over to the generated <see cref="App.Main"/>.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: when Velopack's installer / updater launches the exe with a hook
        // argument it handles it here and exits. A normal launch returns immediately.
        // Auto-apply is OFF — an update is applied only from Home › Version, on an explicit
        // confirm, after the hardware is disconnected (see AppUpdater).
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        App.Main();
    }
}
