using System.Text;
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
        Application.Current.Shutdown();
    }

    private void AppendLine(string line)
    {
        _log.AppendLine(line);
        LogText.Text = _log.ToString();
        LogScroller.ScrollToEnd();
        StatusText.Text = line;
    }
}
