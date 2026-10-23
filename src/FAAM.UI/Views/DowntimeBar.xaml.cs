using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.Workflow.Oee;

namespace FAAM.UI.Views;

/// <summary>
/// The station's live OEE state with the DOWN / UP toggle — on the Engineer page and the
/// OEE page. Declaring DOWN asks for the engineer / technician's name, a category and the
/// reason; bringing it UP shows that and asks for the root cause and the action taken
/// (<see cref="StationStateWindow"/>) — one maintenance-log entry in Layer 2
/// (<see cref="OeeTracker.GoDown"/> / <see cref="OeeTracker.GoUp"/>). The toggle needs
/// <see cref="AccessPolicy.Downtime"/>; the state is visible to anyone who sees the page.
/// </summary>
public partial class DowntimeBar : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private EngineHost? _host;

    public DowntimeBar()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Attach(EngineHost host)
    {
        _host = host;
        host.Process.Oee.Changed += () => Dispatcher.BeginInvoke(Refresh);
        host.Session.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        if (_host == null) return;
        var st = _host.Process.Oee.Status;
        var since = DateTime.UtcNow - st.SinceUtc;

        StateChip.Background = OeeColors.For(st.State);
        StateText.Text = OeeColors.Label(st.State);

        if (st.OpenDowntime is { } d)
        {
            DetailText.Text = $"since {d.Start.ToLocalTime():ddd HH:mm} ({OeeColors.Duration(since)}) · by {d.DownBy}"
                            + (d.Category != null ? $" · {d.Category}" : "")
                            + (d.Reason != null ? $" · {d.Reason}" : "");
            ToggleButton.Content = "Bring UP…";
            ToggleButton.SetResourceReference(BackgroundProperty, "Ok");
        }
        else
        {
            DetailText.Text = st.State switch
            {
                OeeState.Standby => $"nothing executing for {OeeColors.Duration(since)} — idle after {st.IdleThreshold.TotalMinutes:0} min",
                OeeState.Idle => $"nothing executed for {OeeColors.Duration(since)}",
                _ => $"for {OeeColors.Duration(since)}",
            };
            ToggleButton.Content = "Declare DOWN…";
            ToggleButton.SetResourceReference(BackgroundProperty, "Danger");
        }

        var allowed = _host.Session.Has(AccessPolicy.Downtime);
        ToggleButton.IsEnabled = allowed;
        ToggleButton.ToolTip = allowed ? null : $"Needs {AccessPolicy.Downtime} level or above.";
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_host == null || !_host.Session.Has(AccessPolicy.Downtime)) return;
        var oee = _host.Process.Oee;
        var down = oee.Status.OpenDowntime;
        var goingDown = down == null;

        var dlg = new StationStateWindow(down) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var result = goingDown
            ? oee.GoDown(dlg.PersonName, dlg.Category, dlg.Reason)
            : oee.GoUp(dlg.PersonName, dlg.RootCause, dlg.ActionTaken);
        var signedIn = _host.Session.Operator?.Id ?? "-";
        _host.Log.Add(new LogEntry(result.IsSuccess ? LogLevel.Info : LogLevel.Warning, DateTimeOffset.Now, "OEE",
            result.IsSuccess
                ? $"Station {(goingDown ? "DOWN" : "UP")} confirmed by {dlg.PersonName} (signed in: {signedIn})."
                : $"Station {(goingDown ? "DOWN" : "UP")} refused — {result.Message}"));
        Refresh();
    }
}
