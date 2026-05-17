using System.Windows;
using System.Windows.Threading;

namespace Horizon.UI;

/// <summary>
/// Application entry point. Builds the <see cref="EngineHost"/> composition root
/// and shows the <see cref="StartupWindow"/>, which runs the connect sequence
/// and hands off to <see cref="ShellWindow"/>.
/// </summary>
public partial class App : Application
{
    private EngineHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A faulted UI-thread dispatch should never silently kill the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _host = new EngineHost();
        new StartupWindow(_host).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Engine shutdown is async; OnExit is not — block briefly so hardware
        // is stopped and disconnected cleanly before the process ends.
        try
        {
            _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort teardown — never let a shutdown fault mask exit.
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "Horizon — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
