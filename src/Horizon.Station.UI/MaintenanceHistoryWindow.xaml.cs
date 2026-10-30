using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Horizon.Engine;
using Horizon.Workflow.Oee;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Horizon.Station.UI;

/// <summary>
/// The maintenance history — every DOWN → UP entry in <c>oee.db</c> for a date range, with
/// free-text search over category / reason / root cause / action / names, a summary (count,
/// total downtime, MTTR, longest, time by category) and CSV export. Read-only; opened from
/// the OEE page. Queries run off the UI thread.
/// </summary>
public partial class MaintenanceHistoryWindow : Window
{
    private readonly EngineHost _host;
    private IReadOnlyList<OeeDowntime> _rows = Array.Empty<OeeDowntime>();
    private bool _loading;

    public MaintenanceHistoryWindow(EngineHost host)
    {
        InitializeComponent();
        _host = host;
        SetRange(30);
        Loaded += async (_, _) => await LoadAsync();
    }

    private void SetRange(int days)
    {
        ToPicker.SelectedDate = DateTime.Today;
        FromPicker.SelectedDate = DateTime.Today.AddDays(-(days - 1));
    }

    private async void OnQuickRange(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var days)) SetRange(days);
        await LoadAsync();
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        var from = (FromPicker.SelectedDate ?? DateTime.Today.AddDays(-29)).Date;
        var to = (ToPicker.SelectedDate ?? DateTime.Today).Date.AddDays(1);   // inclusive end day
        if (to <= from) (from, to) = (to.AddDays(-1), from.AddDays(1));
        var search = SearchBox.Text;
        var oee = _host.Process.Oee;
        try
        {
            StatusText.Text = "Loading…";
            _rows = await Task.Run(() => oee.GetMaintenanceHistory(from.ToUniversalTime(), to.ToUniversalTime(), search));
            ShowRows();
            StatusText.Text = $"{_rows.Count} entr{(_rows.Count == 1 ? "y" : "ies")} · {from:yyyy-MM-dd} – {to.AddDays(-1):yyyy-MM-dd}" +
                              (string.IsNullOrWhiteSpace(search) ? "" : $" · matching \"{search.Trim()}\"");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            StatusText.Text = $"Cannot read the maintenance history — {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowRows()
    {
        var now = DateTime.UtcNow;
        HistoryGrid.ItemsSource = _rows.Select(d => new Row(
            d.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            d.End?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "(still down)",
            Hm(d.Duration(now)),
            d.Category ?? "", d.Reason ?? "", d.DownBy,
            d.RootCause ?? "", d.ActionTaken ?? d.UpNote ?? "", d.UpBy ?? "")).ToList();

        var closed = _rows.Where(d => !d.IsOpen).ToList();
        var total = TimeSpan.FromTicks(_rows.Sum(d => d.Duration(now).Ticks));
        var mttr = closed.Count == 0 ? (TimeSpan?)null : TimeSpan.FromTicks((long)closed.Average(d => d.Duration(now).Ticks));
        var longest = _rows.Count == 0 ? (TimeSpan?)null : _rows.Max(d => d.Duration(now));

        SummaryText.Text = _rows.Count == 0
            ? "No downtime in this range."
            : $"{_rows.Count} downtime event{(_rows.Count == 1 ? "" : "s")} · total {Hm(total)}" +
              (mttr is { } m ? $" · MTTR {Hm(m)}" : "") +
              (longest is { } l ? $" · longest {Hm(l)}" : "") +
              (_rows.Any(d => d.IsOpen) ? " · 1 still open" : "");

        CategoryText.Text = _rows.Count == 0 ? "" :
            "By category: " + string.Join("   ·   ", _rows
                .GroupBy(d => d.Category ?? "(none)")
                .Select(g => (Name: g.Key, Count: g.Count(), Time: TimeSpan.FromTicks(g.Sum(d => d.Duration(now).Ticks))))
                .OrderByDescending(g => g.Time)
                .Select(g => $"{g.Name} {g.Count}× ({Hm(g.Time)})"));
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export maintenance history",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"maintenance-history-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildCsv(_rows), new UTF8Encoding(false));
            StatusText.Text = $"Exported {_rows.Count} rows to {Path.GetFileName(dialog.FileName)}";
            _host.Log.Add(new LogEntry(LogLevel.Info, DateTimeOffset.Now, "OEE",
                $"Maintenance history: exported {_rows.Count} rows to {dialog.FileName}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private static string BuildCsv(IReadOnlyList<OeeDowntime> rows)
    {
        var now = DateTime.UtcNow;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("down_local,up_local,duration_min,category,reason,down_by,root_cause,action_taken,up_by,note,station");
        foreach (var d in rows)
        {
            sb.AppendJoin(',',
                d.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv),
                d.End?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "",
                d.Duration(now).TotalMinutes.ToString("0.0", inv),
                Csv(d.Category), Csv(d.Reason), Csv(d.DownBy), Csv(d.RootCause), Csv(d.ActionTaken),
                Csv(d.UpBy), Csv(d.UpNote), Csv(d.Station));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string Hm(TimeSpan t) => $"{(int)t.TotalHours}h {t.Minutes:00}m";

    public sealed record Row(string Down, string Up, string Duration, string Category, string Reason,
                             string DownBy, string RootCause, string ActionTaken, string UpBy);
}
