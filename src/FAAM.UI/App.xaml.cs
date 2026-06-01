using System.IO;
using System.Windows;
using System.Windows.Threading;
using Horizon.Engine;
using Serilog;
using Serilog.Events;

namespace FAAM.UI;

/// <summary>
/// Application entry point. Builds the <see cref="EngineHost"/> composition root
/// and shows the <see cref="StartupWindow"/>, which runs the connect sequence and
/// hands off to <see cref="ShellWindow"/>.
/// </summary>
public partial class App : Application
{
    private EngineHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A faulted UI-thread dispatch should never silently kill the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ConfigureLogging();

        _host = new EngineHost();
        // Persist every UI log line (engine + workflow events) to the same file as
        // the lower-level Serilog diagnostics (e.g. the swallowed ACS Kill exception).
        _host.Log.Appended += ForwardToSerilog;
        new StartupWindow(_host).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Engine shutdown is async; OnExit is not — block briefly so hardware is
        // stopped and disconnected cleanly before the process ends.
        try
        {
            if (_host != null)
                _host.Log.Appended -= ForwardToSerilog;
            _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort teardown — never let a shutdown fault mask exit.
        }

        Log.CloseAndFlush();   // flush any buffered log lines before the process ends
        base.OnExit(e);
    }

    /// <summary>
    /// Daily rolling file log at <c>%APPDATA%\Horizon\logs\faam-.log</c> (7-day
    /// retention), mirroring Horizon.App. Captures both the engine/adapter Serilog
    /// diagnostics and the forwarded UI log lines (<see cref="ForwardToSerilog"/>).
    /// </summary>
    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "faam-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>Writes one UI <see cref="LogEntry"/> to the Serilog file sink.</summary>
    private static void ForwardToSerilog(LogEntry entry)
    {
        var level = entry.Level switch
        {
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information,
        };
        Log.Write(level, "[{Source}] {Message}", entry.Source, entry.Message);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "FAAM — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
