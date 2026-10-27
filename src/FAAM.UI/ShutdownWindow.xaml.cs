using System.Text;
using System.Threading;
using System.Windows;

namespace FAAM.UI;

/// <summary>
/// The shutdown / disconnect screen — the mirror of <see cref="StartupWindow"/>.
/// Runs the engine's ordered hardware teardown (<see cref="EngineHost.ShutdownAsync"/>)
/// off the UI thread, streaming a line per step, then ends the process. Shown by
/// <see cref="ShellWindow"/> once the operator confirms exit.
/// </summary>
public partial class ShutdownWindow : Window
{
    private readonly EngineHost _host;
    private readonly StringBuilder _log = new();

    public ShutdownWindow(EngineHost host)
    {
        _host = host;
        InitializeComponent();
        Loaded += async (_, _) => await RunShutdownAsync();
    }

    private async Task RunShutdownAsync()
    {
        var progress = new Progress<string>(AppendLine);   // marshals to this UI thread
        try
        {
            await _host.ShutdownAsync(progress);
        }
        catch (Exception ex)
        {
            // Teardown is best-effort — log the fault and exit regardless.
            AppendLine($"ERROR: {ex.Message}");
        }

        StatusText.Text = "Shutdown complete.";
        Progress.IsIndeterminate = false;
        Progress.Value = Progress.Maximum;

        // Brief pause so the operator sees the final state before the window goes.
        await Task.Delay(600);

        // Hardware is down: hand a downloaded software update to Velopack's updater now —
        // it waits for this process to end (≤ 60 s), installs, and relaunches FAAM.
        AppUpdater.ApplyPendingAtExit();

        // Last-resort exit: if something keeps the dispatcher alive past this point
        // (a stray window, a modal error box, a handler that throws during close),
        // end the process anyway so the operator is never left staring at a
        // finished shutdown screen. Hardware is already disconnected by now.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ExitWatchdogMs);
            Serilog.Log.CloseAndFlush();
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "FAAM exit watchdog" };
        watchdog.Start();

        Application.Current.Shutdown();
    }

    /// <summary>How long after Application.Shutdown() the process is force-ended.</summary>
    private const int ExitWatchdogMs = 5000;

    private void AppendLine(string line)
    {
        _log.AppendLine(line);
        LogText.Text = _log.ToString();
        LogScroller.ScrollToEnd();
        StatusText.Text = line;
    }
}
