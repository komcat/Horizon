using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Engine;
using Horizon.Workflow.Oee;
using Microsoft.Data.Sqlite;

namespace FAAM.UI.Views;

/// <summary>
/// The OEE page (Home › OEE) — how the station spent each 24 h day: a colour timeline,
/// uptime / availability / utilization / quality / OEE tiles, the time per state, the
/// downtime log (who took it down, who brought it up) and a 7-day summary. Reads the
/// Layer 2 <see cref="OeeTracker"/>; reports are built off the UI thread. While today is
/// shown it refreshes every 30 s; the live state badge ticks every second.
/// </summary>
public partial class OeeView : UserControl
{
    private static readonly TimeSpan ReportRefresh = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _refresh = new() { Interval = ReportRefresh };
    private EngineHost? _host;
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private bool _loading;
    private bool _syncingPicker;

    public OeeView()
    {
        InitializeComponent();
        _live.Tick += (_, _) => UpdateLive();
        _refresh.Tick += async (_, _) => { if (_day == Today) await LoadAsync(); };
        BuildLegend();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public void Attach(EngineHost host)
    {
        _host = host;
        Downtime.Attach(host);
        host.Process.Oee.Changed += () => Dispatcher.BeginInvoke(async () =>
        {
            if (_live.IsEnabled && _day == Today) await LoadAsync();
        });
    }

    /// <summary>Called when the shell shows the page — jumps to today and starts the timers.</summary>
    public async void Activate()
    {
        _day = Today;
        SyncPicker();
        UpdateLive();
        _live.Start();
        _refresh.Start();
        await LoadAsync();
    }

    public void Deactivate()
    {
        _live.Stop();
        _refresh.Stop();
    }

    // --- day navigation ---

    private async void OnPrevDay(object sender, RoutedEventArgs e) => await ShowDayAsync(_day.AddDays(-1));

    private async void OnNextDay(object sender, RoutedEventArgs e)
    {
        if (_day < Today) await ShowDayAsync(_day.AddDays(1));
    }

    private async void OnToday(object sender, RoutedEventArgs e) => await ShowDayAsync(Today);

    private async void OnDayPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPicker || DayPicker.SelectedDate is not { } d) return;
        var day = DateOnly.FromDateTime(d);
        await ShowDayAsync(day > Today ? Today : day);
    }

    private async Task ShowDayAsync(DateOnly day)
    {
        _day = day;
        SyncPicker();
        await LoadAsync();
    }

    private void SyncPicker()
    {
        _syncingPicker = true;
        DayPicker.SelectedDate = _day.ToDateTime(TimeOnly.MinValue);
        DayPicker.DisplayDateEnd = DateTime.Today;
        _syncingPicker = false;
    }

    // --- data ---

    private async Task LoadAsync()
    {
        if (_host == null || _loading) return;
        _loading = true;
        var day = _day;
        var oee = _host.Process.Oee;
        try
        {
            var (report, week) = await Task.Run(() =>
            {
                var r = oee.GetDayReport(day);
                var w = Enumerable.Range(0, 7).Select(i => i == 0 ? r : oee.GetDayReport(day.AddDays(-i))).ToList();
                return (r, w);
            });
            if (day != _day) return;   // the operator moved on while this loaded
            Show(report, week, oee.DatabasePath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            WindowText.Text = $"Cannot read OEE data — {ex.Message}";
            _host.Log.Add(new LogEntry(LogLevel.Warning, DateTimeOffset.Now, "OEE", $"OEE report failed: {ex.Message}"));
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(OeeReport r, IReadOnlyList<OeeReport> week, string dbPath)
    {
        WindowText.Text = _day == Today
            ? $"Today so far — {r.Elapsed.TotalHours:0.0} h of {r.Total.TotalHours:0} h elapsed"
            : $"{_day:ddd yyyy-MM-dd} — {r.Total.TotalHours:0} h";

        Timeline.Report = r;

        UptimeValue.Text = Pct(r.UptimeRatio);
        AvailabilityValue.Text = Pct(r.Availability);
        UtilizationValue.Text = Pct(r.Utilization);
        QualityValue.Text = r.Quality is { } q ? Pct(q) : "—";
        QualityCaption.Text = $"Quality — {r.Passed} pass / {r.Failed} fail" + (r.Aborted > 0 ? $" ({r.Aborted} aborted)" : "");
        OeeValue.Text = r.Oee is { } o ? Pct(o) : "—";

        BreakdownGrid.ItemsSource = new[]
        {
            Row(OeeState.Running, r.Running), Row(OeeState.Standby, r.Standby), Row(OeeState.Idle, r.Idle),
            Row(OeeState.Down, r.Down), Row(OeeState.Offline, r.Offline),
        };

        DowntimeGrid.ItemsSource = r.Downtimes.Select(d => new DowntimeRow(
            d.Start.ToLocalTime().ToString("ddd HH:mm"),
            d.End?.ToLocalTime().ToString("ddd HH:mm") ?? "(still down)",
            OeeColors.Duration((d.End ?? DateTime.UtcNow) - d.Start),
            d.Category ?? "", d.Reason ?? "", d.RootCause ?? "", d.ActionTaken ?? d.UpNote ?? "",
            d.UpBy is { } up ? $"{d.DownBy} / {up}" : d.DownBy)).ToList();
        NoDowntimeText.Visibility = r.Downtimes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DowntimeGrid.Visibility = r.Downtimes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        WeekGrid.ItemsSource = week.Select(w => new DayRow(
            w.From.ToLocalTime().ToString("ddd yyyy-MM-dd"),
            Hm(w.Running), Hm(w.Standby), Hm(w.Idle), Hm(w.Down), Hm(w.Offline),
            Pct(w.UptimeRatio), Pct(w.Utilization),
            $"{w.Passed} / {w.Failed}",
            w.Quality is { } wq ? Pct(wq) : "—",
            w.Oee is { } wo ? Pct(wo) : "—")).ToList();

        DefinitionsText.Text =
            "Total available time = 24 h. Running = a program / deck / recipe / script / Run-page step / alignment executing. " +
            $"Standby = online with nothing executing for under {r.IdleThreshold.TotalMinutes:0} min (handling between units — counts as production). " +
            $"Idle = nothing executed for {r.IdleThreshold.TotalMinutes:0} min or more (the whole gap). " +
            "Down = declared by an engineer / technician (wins over everything, even while FAAM is closed). Offline = FAAM not running. " +
            "Uptime = (production + idle) ÷ elapsed · Availability = 1 − down ÷ elapsed · Utilization = production ÷ elapsed · " +
            "Quality = pass ÷ (pass + fail) · OEE = utilization × quality (performance not tracked). " +
            $"Data: {dbPath}";

        BreakdownRow Row(OeeState s, TimeSpan t) => new(
            OeeColors.For(s), OeeColors.Label(s), Hm(t),
            r.Elapsed > TimeSpan.Zero ? Pct(t / r.Elapsed) : "—",
            r.Total > TimeSpan.Zero ? Pct(t / r.Total) : "—");
    }

    private void OnMaintenanceHistory(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        new MaintenanceHistoryWindow(_host) { Owner = Window.GetWindow(this) }.Show();
    }

    private void UpdateLive()
    {
        if (_host == null) return;
        var st = _host.Process.Oee.Status;
        LiveBadge.Background = OeeColors.For(st.State);
        LiveStateText.Text = OeeColors.Label(st.State);
        LiveSinceText.Text = $"since {st.SinceUtc.ToLocalTime():ddd HH:mm} · {OeeColors.Duration(DateTime.UtcNow - st.SinceUtc)}";
    }

    private void BuildLegend()
    {
        foreach (var s in new[] { OeeState.Running, OeeState.Standby, OeeState.Idle, OeeState.Down, OeeState.Offline })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            item.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                Background = OeeColors.For(s), VerticalAlignment = VerticalAlignment.Center,
            });
            var label = new TextBlock { Text = OeeColors.Label(s), Margin = new Thickness(5, 0, 0, 0), FontSize = 11.5 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Subtle");
            item.Children.Add(label);
            Legend.Children.Add(item);
        }
    }

    private static string Pct(double v) => $"{v * 100:0.0} %";

    private static string Hm(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";

    public sealed record BreakdownRow(Brush Swatch, string State, string Time, string OfElapsed, string OfDay);

    public sealed record DowntimeRow(string Start, string End, string Duration, string Category, string Reason,
                                     string RootCause, string ActionTaken, string People);

    public sealed record DayRow(string Day, string Running, string Standby, string Idle, string Down, string Offline,
                                 string Uptime, string Utilization, string Units, string Quality, string Oee);
}
