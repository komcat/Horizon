using System.Linq;
using System.Text;
using System.Windows;

namespace FAAM.UI;

/// <summary>
/// The startup / connect screen. Runs the engine bring-up sequence
/// (<see cref="EngineHost.InitializeAndConnectAsync"/>) off the UI thread,
/// streaming progress, then hands off to <see cref="ShellWindow"/>. On failure it
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
        // Auto sign-in, when enabled: only ever for a user whose LEVEL HAS NO PASSWORD.
        // A level with a password must be typed in, or the setting would hand out that
        // level for free and the whole gate would be decorative. Once every level is
        // given a password, startup always asks - which is the point.
        if (_host.Settings.Current.AutoLoginOperator
            && !_host.Session.IsLoggedIn
            && _host.Users.ActiveUsers.FirstOrDefault(u => !_host.Users.HasLevelPassword(u.Level)) is { } op)
        {
            _host.Session.LogIn(op);
        }

        // Hardware is up; nothing in the shell works until somebody signs in, so gate it
        // here. Cancelling the first sign-in exits the app rather than opening a shell
        // where every screen is locked.
        if (!_host.Session.IsLoggedIn)
        {
            var login = new LoginWindow(_host) { Owner = this };
            if (login.ShowDialog() != true)
            {
                Application.Current.Shutdown();
                return;
            }
        }

        var shell = new ShellWindow(_host);
        Application.Current.MainWindow = shell;
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
