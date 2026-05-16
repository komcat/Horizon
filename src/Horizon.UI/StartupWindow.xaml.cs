using System.Text;
using System.Windows;

namespace Horizon.UI;

/// <summary>
/// The startup / connect screen. Runs the engine bring-up sequence
/// (<see cref="EngineHost.InitializeAndConnectAsync"/>) off the UI thread,
/// streaming progress, then hands off to <see cref="MainWindow"/>. On failure it
/// offers Retry / Exit rather than leaving a dead window.
/// </summary>
public partial class StartupWindow : Window
{
    private readonly EngineHost _host;
    private readonly StringBuilder _log = new();

    public StartupWindow(EngineHost host)
    {
        _host = host;
        InitializeComponent();
        Loaded += async (_, _) => await RunConnectAsync();
    }

    private async Task RunConnectAsync()
    {
        ShowConnecting();

        var progress = new Progress<string>(AppendLine);   // marshals to this UI thread
        try
        {
            await _host.InitializeAndConnectAsync(progress);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }

        HandOffToShell();
    }

    private void HandOffToShell()
    {
        var shell = new ShellWindow(_host);
        Application.Current.MainWindow = shell;   // so OnMainWindowClose tracks the real window
        shell.Show();
        Close();
    }

    private void ShowConnecting()
    {
        StatusText.Text = "Connecting hardware…";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorButtons.Visibility = Visibility.Collapsed;
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = "Startup failed.";
        Progress.IsIndeterminate = false;
        Progress.Visibility = Visibility.Collapsed;
        ErrorText.Text = ex.Message;
        ErrorText.Visibility = Visibility.Visible;
        ErrorButtons.Visibility = Visibility.Visible;
        AppendLine($"ERROR: {ex.Message}");
    }

    private void AppendLine(string line)
    {
        _log.AppendLine(line);
        LogText.Text = _log.ToString();
        LogScroller.ScrollToEnd();
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
        => await RunConnectAsync();

    private void OnExitClick(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();
}
