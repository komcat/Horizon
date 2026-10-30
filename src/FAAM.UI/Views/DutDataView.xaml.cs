using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Horizon.Engine;
using Horizon.Workflow;
using Horizon.Workflow.Signals;
using Microsoft.Win32;

namespace FAAM.UI.Views;

/// <summary>
/// The DUT Data page — the UV-cure signal snapshots (<c>uv1.0</c> before cure 1,
/// <c>uv1.1</c> after it, <c>uv2.0</c> / <c>uv2.1_final</c> around cure 2) of every unit of
/// the active product. The overview lists <b>one row per UV run</b>, newest first (a
/// reworked serial appears once per run), showing the picked signal at each snapshot plus
/// the final-minus-initial delta; selecting a row lists that run's snapshots with every
/// signal, and the chart trends the picked signal across all rows shown. Rows can be
/// exported to CSV. Read-only: it only calls <see cref="IProcessService.ListDutRecords"/> /
/// <see cref="IProcessService.GetDutRecord"/>, on a background thread — the product has
/// hundreds of record files — with progress in the status text and a cancel on leaving.
/// </summary>
public partial class DutDataView : UserControl, IStationPage
{
    private const int ProgressEvery = 25;

    private EngineHost? _host;
    private bool _visible;
    private bool _stale = true;
    private int _loadGeneration;
    private CancellationTokenSource? _loadCts;

    private IReadOnlyList<UvRun> _runs = Array.Empty<UvRun>();      // newest first
    private List<OverviewRow> _visibleRows = new();
    private IReadOnlyList<string> _signalKeys = Array.Empty<string>();
    private bool _suppressSignalChanged;
    private RangePreset _range = RangePreset.All;

    /// <summary>The overview's range presets — a date window or the newest N runs.</summary>
    private enum RangePreset { All, Week, Month, Ytd, Last30, Last100 }

    /// <summary>A signal picker entry — key plus the human label + unit from the signal catalog.</summary>
    private sealed record SignalChoice(string Key, string Label, string Unit);

    /// <summary>One overview row — the run plus the picked signal at each of the four snapshots.</summary>
    private sealed class OverviewRow
    {
        public required UvRun Run { get; init; }
        public string Serial => Run.Serial;
        public string Started => Run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string Source => Run.Source;
        public string Verdict => Run.Verdict.ToString();
        public string Operator => Run.Operator;
        public double? Uv10 { get; init; }
        public double? Uv11 { get; init; }
        public double? Uv20 { get; init; }
        public double? Uv21Final { get; init; }
        public double? Delta => Uv21Final is double f && Uv10 is double i ? f - i : null;
    }

    public DutDataView() => InitializeComponent();

    /// <summary>Supplies the engine host (Layer 2 access) and follows product switches.</summary>
    public void Attach(EngineHost host)
    {
        _host = host;
        _host.Process.ProductChanged += OnProductChanged;
        _host.Process.SiphogSnapshotSaved += OnSiphogSnapshotSaved;
        SnapshotsView.Attach(host);
    }

    /// <summary>A snapshot was just written (any thread) — refresh the SiPhOG tab live when it's on screen.</summary>
    private void OnSiphogSnapshotSaved(string serial)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_visible && ReferenceEquals(Tabs.SelectedItem, SnapshotsTab)) SnapshotsView.Reload();
        });
    }

    /// <summary>Loads (or reloads, after a product switch) the product's records when the page is shown.</summary>
    public void Activate()
    {
        _visible = true;
        RefreshTitle();
        if (_stale) BeginLoad();
        if (ReferenceEquals(Tabs.SelectedItem, SnapshotsTab)) SnapshotsView.Reload();
    }

    /// <summary>The SiPhOG snapshots tab re-lists its serials each time it's shown (cheap: one dir listing).</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;   // ignore bubbling from inner grids / lists
        if (_visible && ReferenceEquals(Tabs.SelectedItem, SnapshotsTab)) SnapshotsView.Reload();
    }

    /// <summary>Stops an in-flight load; the next <see cref="Activate"/> starts over.</summary>
    public void Deactivate()
    {
        _visible = false;
        CancelLoad();
    }

    private void OnProductChanged(string? product)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _stale = true;
            _runs = Array.Empty<UvRun>();
            SnapshotsView.Clear();
            if (_visible && ReferenceEquals(Tabs.SelectedItem, SnapshotsTab)) SnapshotsView.Reload();
            if (_visible) BeginLoad();
            else ClearViews();
        });
    }

    private void RefreshTitle()
        => TitleText.Text = $"DUT Data — product '{_host?.Process.ActiveProduct ?? "(none)"}'";

    // ---------------------------------------------------------------- loading

    private void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void BeginLoad()
    {
        if (_host == null) return;
        CancelLoad();

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        int generation = ++_loadGeneration;

        RefreshTitle();
        StatusText.Text = "Loading…";
        ExportButton.IsEnabled = false;
        _ = LoadAsync(_host.Process, generation, cts.Token);
    }

    private async Task LoadAsync(ProcessService process, int generation, CancellationToken ct)
    {
        try
        {
            var scan = await Task.Run(() => Scan(process, generation, ct), ct);
            if (generation != _loadGeneration) return;   // superseded by a newer load

            _runs = scan.Runs;
            _stale = false;
            var serials = CountSerials(_runs);
            StatusText.Text = scan.Failed == 0
                ? $"{scan.Serials} records · {serials} serials with UV data · {_runs.Count} UV runs"
                : $"{scan.Serials} records · {serials} serials with UV data · {_runs.Count} UV runs · {scan.Failed} unreadable";
            RebuildSignals();
            RebuildRows();
            ExportButton.IsEnabled = _visibleRows.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // Left the page or refreshed mid-load — the next Activate reloads.
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            StatusText.Text = $"Load failed: {ex.Message}";
            Log(LogLevel.Warning, $"DUT Data load failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Background scan: every record of the active product → its UV runs, newest first.
    /// Bounded by the serial list; a broken record file is counted and skipped, never fatal.
    /// </summary>
    private (List<UvRun> Runs, int Serials, int Failed) Scan(ProcessService process, int generation, CancellationToken ct)
    {
        var serials = process.ListDutRecords();
        var runs = new List<UvRun>();
        int failed = 0;

        for (int i = 0; i < serials.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = process.GetDutRecord(serials[i]);
                if (record != null) runs.AddRange(DutUvData.Extract(record));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                Log(LogLevel.Warning, $"DUT record '{serials[i]}' skipped: {ex.Message}");
            }

            if ((i + 1) % ProgressEvery == 0)
            {
                int done = i + 1, total = serials.Count;
                Dispatcher.InvokeAsync(() =>
                {
                    if (generation == _loadGeneration) StatusText.Text = $"Loading {done}/{total}…";
                });
            }
        }

        runs.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
        return (runs, serials.Count, failed);
    }

    private static int CountSerials(IReadOnlyList<UvRun> runs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in runs) set.Add(r.Serial);
        return set.Count;
    }

    // ---------------------------------------------------------------- signal picker

    /// <summary>
    /// Fills the Signal combo with every signal key the loaded snapshots contain (SiPhOG keys
    /// first), labelled from the signal catalog; keeps the current pick when it survives,
    /// else falls back to SAG-PWR.
    /// </summary>
    private void RebuildSignals()
    {
        var current = (SignalCombo.SelectedItem as SignalChoice)?.Key ?? DutUvData.DefaultSignal;
        _signalKeys = DutUvData.SignalKeys(_runs);

        var choices = new List<SignalChoice>(_signalKeys.Count);
        foreach (var key in _signalKeys)
        {
            var d = Describe(key);
            var label = d == null ? key : string.IsNullOrEmpty(d.Unit) ? d.Display : $"{d.Display} ({d.Unit})";
            choices.Add(new SignalChoice(key, label, d?.Unit ?? ""));
        }

        _suppressSignalChanged = true;
        try
        {
            SignalCombo.ItemsSource = choices;
            SignalCombo.SelectedItem = choices.Find(c => string.Equals(c.Key, current, StringComparison.OrdinalIgnoreCase))
                                       ?? choices.Find(c => string.Equals(c.Key, DutUvData.DefaultSignal, StringComparison.OrdinalIgnoreCase))
                                       ?? (choices.Count > 0 ? choices[0] : null);
        }
        finally { _suppressSignalChanged = false; }
    }

    private SignalDescriptor? Describe(string key)
    {
        if (_host == null) return null;
        foreach (var d in _host.Process.DescribeSignals())
            if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    private SignalChoice? SelectedSignal => SignalCombo.SelectedItem as SignalChoice;

    // ---------------------------------------------------------------- overview + chart

    /// <summary>Rebuilds the overview rows (filter + picked signal) and the chart; preserves the selected run.</summary>
    private void RebuildRows()
    {
        var signal = SelectedSignal;
        var key = signal?.Key ?? DutUvData.DefaultSignal;
        var unit = signal?.Unit ?? "";
        var filter = FilterBox.Text.Trim();
        var selected = (OverviewGrid.SelectedItem as OverviewRow)?.Run;

        var (cutoffUtc, maxRuns) = RangeBounds(_range);

        var rows = new List<OverviewRow>();
        foreach (var run in _runs)   // newest first, so a count cap keeps the newest
        {
            if (rows.Count >= maxRuns) break;
            if (run.StartedUtc < cutoffUtc) continue;
            if (filter.Length > 0 && !run.Serial.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new OverviewRow
            {
                Run = run,
                Uv10 = run.Value(DutUvData.SnapshotKeys[0], key),
                Uv11 = run.Value(DutUvData.SnapshotKeys[1], key),
                Uv20 = run.Value(DutUvData.SnapshotKeys[2], key),
                Uv21Final = run.Value(DutUvData.SnapshotKeys[3], key),
            });
        }
        _visibleRows = rows;

        var suffix = string.IsNullOrEmpty(unit) ? "" : $" ({unit})";
        Uv10Column.Header = DutUvData.SnapshotKeys[0] + suffix;
        Uv11Column.Header = DutUvData.SnapshotKeys[1] + suffix;
        Uv20Column.Header = DutUvData.SnapshotKeys[2] + suffix;
        Uv21Column.Header = DutUvData.SnapshotKeys[3] + suffix;
        DeltaColumn.Header = $"Δ {DutUvData.SnapshotKeys[3]} − {DutUvData.SnapshotKeys[0]}{suffix}";

        var scope = RangeLabel(_range);
        if (filter.Length > 0) scope += $" matching '{filter}'";
        OverviewHeader.Text = _range == RangePreset.All && filter.Length == 0
            ? $"{rows.Count} UV runs · {CountSerials(_runs)} serials — {signal?.Label ?? key}"
            : $"{rows.Count} UV runs · {CountSerials(rows.ConvertAll(r => r.Run))} serials ({scope}) — {signal?.Label ?? key}";

        OverviewGrid.ItemsSource = rows;
        ExportButton.IsEnabled = rows.Count > 0;

        if (selected != null)
        {
            var again = rows.Find(r => ReferenceEquals(r.Run, selected));
            if (again != null)
            {
                OverviewGrid.SelectedItem = again;
                OverviewGrid.ScrollIntoView(again);
            }
        }

        UpdateChart(key, unit);
        if (OverviewGrid.SelectedItem == null) ClearDetail();
    }

    /// <summary>The earliest start (UTC) and the most runs a preset admits.</summary>
    private static (DateTime CutoffUtc, int MaxRuns) RangeBounds(RangePreset range)
    {
        var now = DateTime.Now;
        return range switch
        {
            RangePreset.Week => (now.AddDays(-7).ToUniversalTime(), int.MaxValue),
            RangePreset.Month => (now.AddDays(-30).ToUniversalTime(), int.MaxValue),
            RangePreset.Ytd => (new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), int.MaxValue),
            RangePreset.Last30 => (DateTime.MinValue, 30),
            RangePreset.Last100 => (DateTime.MinValue, 100),
            _ => (DateTime.MinValue, int.MaxValue),
        };
    }

    private static string RangeLabel(RangePreset range) => range switch
    {
        RangePreset.Week => "last 7 days",
        RangePreset.Month => "last 30 days",
        RangePreset.Ytd => "year to date",
        RangePreset.Last30 => "last 30 runs",
        RangePreset.Last100 => "last 100 runs",
        _ => "all",
    };

    /// <summary>Plots the picked signal across the visible rows, oldest on the left, one series per snapshot key.</summary>
    private void UpdateChart(string signalKey, string unit)
    {
        int n = _visibleRows.Count;
        var labels = new string[n];
        var series = new List<ChartSeries>(DutUvData.SnapshotKeys.Length);
        for (int k = 0; k < DutUvData.SnapshotKeys.Length; k++)
            series.Add(new ChartSeries(DutUvData.SnapshotKeys[k], DutTrendChart.Palette[k % DutTrendChart.Palette.Length], new double?[n]));

        for (int i = 0; i < n; i++)
        {
            var run = _visibleRows[n - 1 - i].Run;   // grid is newest first; chart is oldest first
            labels[i] = run.Serial;
            for (int k = 0; k < DutUvData.SnapshotKeys.Length; k++)
                series[k].Values[i] = run.Value(DutUvData.SnapshotKeys[k], signalKey);
        }

        Chart.SetSeries(unit, labels, series);
        Chart.SetHighlight(ChartIndexOf(OverviewGrid.SelectedItem as OverviewRow));
    }

    private int ChartIndexOf(OverviewRow? row)
    {
        if (row == null) return -1;
        int idx = _visibleRows.IndexOf(row);
        return idx < 0 ? -1 : _visibleRows.Count - 1 - idx;
    }

    // ---------------------------------------------------------------- detail

    /// <summary>Lists the selected run's snapshots — one line each, one column per signal it carries.</summary>
    private void ShowDetail(UvRun run)
    {
        var keys = DutUvData.SignalKeys(new[] { run });

        DetailGrid.Columns.Clear();
        DetailGrid.Columns.Add(new DataGridTextColumn { Header = "Snapshot", Binding = new Binding("[0]"), Width = 100 });
        DetailGrid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding("[1]"), Width = 150 });
        for (int i = 0; i < keys.Count; i++)
        {
            var d = Describe(keys[i]);
            var header = d == null ? keys[i] : string.IsNullOrEmpty(d.Unit) ? d.Display : $"{d.Display} ({d.Unit})";
            DetailGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"[{i + 2}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 90,
            });
        }

        var rows = new List<string[]>(run.Snapshots.Count);
        foreach (var snap in run.Snapshots)
        {
            var cells = new string[keys.Count + 2];
            cells[0] = snap.Key;
            cells[1] = snap.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            for (int i = 0; i < keys.Count; i++)
                cells[i + 2] = snap.Values.TryGetValue(keys[i], out var v) ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "";
            rows.Add(cells);
        }
        DetailGrid.ItemsSource = rows;

        DetailHeader.Text = $"Serial {run.Serial} — {run.Source}, {run.Verdict}, started {run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {run.Snapshots.Count} snapshot(s)";
    }

    private void ClearDetail()
    {
        DetailGrid.ItemsSource = null;
        DetailGrid.Columns.Clear();
        DetailHeader.Text = "Select a run above.";
        Chart.SetHighlight(-1);
    }

    private void ClearViews()
    {
        _visibleRows = new List<OverviewRow>();
        OverviewGrid.ItemsSource = null;
        OverviewHeader.Text = "UV runs";
        StatusText.Text = "";
        ExportButton.IsEnabled = false;
        Chart.SetSeries("", Array.Empty<string>(), Array.Empty<ChartSeries>());
        ClearDetail();
    }

    // ---------------------------------------------------------------- events

    private void OnRefresh(object sender, RoutedEventArgs e) => BeginLoad();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = _host?.Process.DutDataDirectory;
        var error = FolderOpener.Open(dir);
        StatusText.Text = error == null ? $"Opened {dir}" : $"Open folder failed: {error}";
        if (error != null) Log(LogLevel.Warning, $"DUT Data: open folder failed: {error}");
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _runs.Count == 0) return;
        RebuildRows();
    }

    private void OnSignalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSignalChanged || !IsLoaded) return;
        RebuildRows();
    }

    private void OnRangeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out RangePreset range)) return;
        _range = range;
        if (!IsLoaded || _runs.Count == 0) return;   // the default "All" checks during XAML parse
        RebuildRows();
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewGrid.SelectedItem is OverviewRow row)
        {
            ShowDetail(row.Run);
            Chart.SetHighlight(ChartIndexOf(row));
        }
        else
        {
            ClearDetail();
        }
    }

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Writes the rows currently shown (filter applied, newest first) as CSV: run identity,
    /// then for every snapshot key its time and every signal — so the file carries all the
    /// data, not just the picked signal.
    /// </summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_visibleRows.Count == 0) return;

        var product = _host?.Process.ActiveProduct ?? "product";
        var dialog = new SaveFileDialog
        {
            Title = "Export DUT UV data",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"dut-uv-{Sanitize(product)}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var runs = _visibleRows.ConvertAll(r => r.Run);
            var signals = DutUvData.SignalKeys(runs);
            File.WriteAllText(dialog.FileName, BuildCsv(runs, signals), new UTF8Encoding(false));
            StatusText.Text = $"Exported {runs.Count} rows to {Path.GetFileName(dialog.FileName)}";
            Log(LogLevel.Info, $"DUT Data: exported {runs.Count} UV runs to {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
            Log(LogLevel.Warning, $"DUT Data export failed: {ex.Message}");
        }
    }

    private static string BuildCsv(IReadOnlyList<UvRun> runs, IReadOnlyList<string> signals)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.Append("Serial,Started,Source,Verdict,Operator");
        foreach (var key in DutUvData.SnapshotKeys)
        {
            sb.Append(',').Append(Csv($"{key} time"));
            foreach (var sig in signals) sb.Append(',').Append(Csv($"{key} {sig}"));
        }
        sb.AppendLine();

        foreach (var run in runs)
        {
            sb.Append(Csv(run.Serial)).Append(',')
              .Append(run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',')
              .Append(Csv(run.Source)).Append(',')
              .Append(run.Verdict).Append(',')
              .Append(Csv(run.Operator));
            foreach (var key in DutUvData.SnapshotKeys)
            {
                var snap = run.Find(key);
                sb.Append(',').Append(snap == null ? "" : snap.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv));
                foreach (var sig in signals)
                {
                    sb.Append(',');
                    if (snap != null && snap.Values.TryGetValue(sig, out var v)) sb.Append(v.ToString("R", inv));
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string s)
        => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "FAAM", message));
}
