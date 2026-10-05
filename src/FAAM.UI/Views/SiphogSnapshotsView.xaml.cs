using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Horizon.Engine;
using Horizon.Workflow;
using Microsoft.Win32;
using SkiaSharp;

namespace FAAM.UI.Views;

/// <summary>
/// DUT Data › SiPhOG snapshots — reviews the labelled SiPhOG sets that the
/// <c>siphog.saveSnapshot</c> block / <c>machine.siphog.save</c> append to the product's
/// <c>siphog_data\{serial}.json</c>. Pick a serial; its sets show as columns (chronological,
/// newest set per label unless "Every set" is on) against the six SiPhOG parameters
/// (mean ± std, optional min / max rows) plus SAG % of target, run, time, operator, window and
/// source. A re-run serial has several runs (one per serial entry); the table shows the latest
/// run unless an earlier run / All runs is picked, so two attempts never mix in one view.
/// Read-only; file reads run off the UI thread. Export writes every set of the listed serials
/// in long form (one row per set × parameter, with its run).
/// </summary>
public partial class SiphogSnapshotsView : UserControl
{
    // Panel order + display names for the six value keys (units come from the file).
    private static readonly (string Key, string Name)[] Params =
    {
        ("sledCurrentMa",  "SLED Current"),
        ("photoCurrentUa", "Photo Current"),
        ("sledTempC",      "SLED Temp"),
        ("targetSagPwrV",  "Target SAG PWR"),
        ("sagPwrV",        "SAG PWR"),
        ("tecCurrentMa",   "TEC Current"),
    };

    private static readonly Dictionary<string, string> DefaultUnits = new()
    {
        ["sledCurrentMa"] = "mA", ["photoCurrentUa"] = "µA", ["sledTempC"] = "°C",
        ["targetSagPwrV"] = "V", ["sagPwrV"] = "V", ["tecCurrentMa"] = "mA",
    };

    private const string AllLabels = "All labels";

    // Line colours per label — the trend chart's four, then more distinct hues.
    private static readonly SKColor[] LabelColors = DutTrendChart.Palette.Concat(new SKColor[]
    {
        new(0xE5, 0x73, 0x73), new(0xFF, 0xF1, 0x76), new(0x4D, 0xB6, 0xAC),
        new(0xF0, 0x62, 0x92), new(0x90, 0xA4, 0xAE), new(0xAE, 0xD5, 0x81),
    }).ToArray();

    private EngineHost? _host;
    private IReadOnlyList<string> _serials = Array.Empty<string>();
    private IReadOnlyList<SiphogSnapshotFile> _allFiles = Array.Empty<SiphogSnapshotFile>();   // every DUT, for the charts
    private SiphogSnapshotFile? _file;
    private int _generation;       // per-serial table loads
    private int _loadGeneration;   // whole-list reloads (Refresh / snapshot saved)

    // Run picker: a run number, or one of these. Latest follows new runs as they arrive.
    private const int LatestRun = -1;
    private const int AllRuns = 0;
    private int _runPick = LatestRun;
    private bool _suppressRunPick;

    private sealed record RunChoice(int Key, string Text)
    {
        public override string ToString() => Text;
    }
    private readonly DutTrendChart[] _charts = new DutTrendChart[Params.Length];
    private bool _suppressChartOptions = true;   // radio/combo events fire during InitializeComponent

    public SiphogSnapshotsView()
    {
        InitializeComponent();
        for (int i = 0; i < Params.Length; i++)
        {
            _charts[i] = new DutTrendChart { EmptyText = "No snapshots" };
            var title = new TextBlock
            {
                Text = Params[i].Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 2, 3),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var cell = new DockPanel { Margin = new Thickness(3) };
            DockPanel.SetDock(title, Dock.Top);
            cell.Children.Add(title);
            cell.Children.Add(_charts[i]);
            ChartGrid.Children.Add(cell);
        }
        LabelCombo.ItemsSource = new[] { AllLabels };
        LabelCombo.SelectedIndex = 0;
        _suppressChartOptions = false;
    }

    public void Attach(EngineHost host) => _host = host;

    /// <summary>Reloads the serial list (keeps the selection when it still exists).</summary>
    public void Reload() => _ = LoadSerialsAsync();

    /// <summary>Drops everything shown (product switched while hidden).</summary>
    public void Clear()
    {
        ++_loadGeneration;   // an in-flight reload belongs to the old product
        ++_generation;
        _serials = Array.Empty<string>();
        _allFiles = Array.Empty<SiphogSnapshotFile>();
        _file = null;
        _runPick = LatestRun;
        SerialList.ItemsSource = null;
        SetRunChoices(Array.Empty<SiphogSnapshotRun>());
        SetGrid.Columns.Clear();
        SetGrid.ItemsSource = null;
        HeaderText.Text = "Select a DUT serial.";
        CountText.Text = StatusText.Text = ChartStatus.Text = string.Empty;
        RenderCharts();
    }

    private async Task LoadSerialsAsync()
    {
        if (_host == null) return;
        var process = _host.Process;
        var load = ++_loadGeneration;
        ChartStatus.Text = "Loading…";
        IReadOnlyList<string> serials;
        IReadOnlyList<SiphogSnapshotFile> files;
        try
        {
            // One small file per DUT — read them all (off the UI thread) so the charts span every DUT.
            (serials, files) = await Task.Run(() =>
            {
                var s = process.ListSiphogSnapshotSerials()
                    .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var f = s.Select(process.GetSiphogSnapshots)
                    .Where(x => x != null).Cast<SiphogSnapshotFile>().ToList();
                return ((IReadOnlyList<string>)s, (IReadOnlyList<SiphogSnapshotFile>)f);
            });
        }
        catch (Exception ex)
        {
            if (load != _loadGeneration) return;
            StatusText.Text = $"Could not list snapshot files: {ex.Message}";
            ChartStatus.Text = string.Empty;
            return;
        }
        if (load != _loadGeneration) return;   // a newer reload (e.g. another snapshot saved) superseded this one

        // Read the selection only now: the operator may have picked a serial while the files loaded.
        var keep = SerialList.SelectedItem as string;
        (_serials, _allFiles) = (serials, files);
        RefreshLabelChoices();
        ApplyFilter();   // swapping ItemsSource keeps an equal string selected — no SelectionChanged fires

        if (keep != null && SerialList.Items.Contains(keep))
        {
            // Show the freshly read file for the kept serial directly; re-selecting it would be a
            // no-op (same item) and leave the table on the stale copy.
            ++_generation;   // drop any in-flight per-serial load — this data is newer
            _file = _allFiles.FirstOrDefault(f => string.Equals(f.Serial, keep, StringComparison.OrdinalIgnoreCase));
            SerialList.SelectedItem = keep;
            Render();
            if (ByDutRadio.IsChecked == true) RenderCharts();   // re-place the selected-DUT marker
        }
        else { _file = null; Render(); }
    }

    private void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        var shown = string.IsNullOrEmpty(f)
            ? _serials.ToList()
            : _serials.Where(s => s.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        SerialList.ItemsSource = shown;
        CountText.Text = $"{shown.Count} of {_serials.Count} serial(s)";
        RenderCharts();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Render();

    private async void OnSerialSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || SerialList.SelectedItem is not string serial) return;
        var gen = ++_generation;
        var process = _host.Process;
        HeaderText.Text = $"DUT {serial} — loading…";
        SiphogSnapshotFile? file;
        try { file = await Task.Run(() => process.GetSiphogSnapshots(serial)); }
        catch (Exception ex) { file = null; StatusText.Text = ex.Message; }
        if (gen != _generation) return;   // a newer selection superseded this load
        if (!string.Equals(_file?.Serial, serial, StringComparison.OrdinalIgnoreCase))
            _runPick = LatestRun;         // another DUT — start on its latest run
        _file = file;
        Render();
        if (ByDutRadio.IsChecked == true) RenderCharts();   // move the selected-DUT marker
    }

    // ---------------------------------------------------------------- grid

    private void OnRunPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRunPick || RunCombo.SelectedItem is not RunChoice c) return;
        _runPick = c.Key;
        Render();
    }

    /// <summary>Re-fills the run picker (Latest, All, then each run newest first), keeping the pick.</summary>
    private void SetRunChoices(IReadOnlyList<SiphogSnapshotRun> runs)
    {
        var items = new List<RunChoice>();
        if (runs.Count > 0)
        {
            items.Add(new RunChoice(LatestRun, $"Latest run (Run {runs.Count})"));
            items.Add(new RunChoice(AllRuns, $"All runs ({runs.Count})"));
            items.AddRange(runs.Reverse().Select(r => new RunChoice(r.Number,
                $"Run {r.Number} · {r.Started.ToLocalTime():MM-dd HH:mm} · {r.Sets.Count} set(s)" +
                (r.Tracked ? "" : " · untracked"))));
        }
        var pick = items.FirstOrDefault(i => i.Key == _runPick) ?? items.FirstOrDefault();
        _runPick = pick?.Key ?? LatestRun;
        _suppressRunPick = true;
        RunCombo.ItemsSource = items;
        RunCombo.SelectedItem = pick;
        _suppressRunPick = false;
    }

    private void Render()
    {
        SetGrid.Columns.Clear();
        SetGrid.ItemsSource = null;
        if (_file == null)
        {
            SetRunChoices(Array.Empty<SiphogSnapshotRun>());
            HeaderText.Text = SerialList.SelectedItem is string s ? $"DUT {s} — file unreadable." : "Select a DUT serial.";
            return;
        }

        var runs = _file.Runs();
        SetRunChoices(runs);
        var shownRuns = _runPick == AllRuns ? runs
            : runs.Where(r => r.Number == (_runPick == LatestRun ? runs.Count : _runPick)).ToList();
        bool every = AllSetsCheck.IsChecked == true;
        var cols = shownRuns.SelectMany(r => SelectSets(r.Sets, every).Select(x => (Run: r, Set: x))).ToList();

        var labels = _file.Sets.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        HeaderText.Text = $"DUT {_file.Serial} — {runs.Count} run(s), {_file.Sets.Count} set(s), {labels} label(s)";
        StatusText.Text = RunStatus(runs, shownRuns, cols.Count);

        var sets = cols.Select(c => c.Set).ToList();
        var table = BuildTable(_file, sets, cols.Select(c => c.Run).ToList());

        SetGrid.Columns.Add(new DataGridTextColumn { Header = "Parameter", Binding = new Binding("p"), MinWidth = 170 });
        for (int i = 0; i < cols.Count; i++)
        {
            var (run, s) = cols[i];
            var head = _runPick == AllRuns ? $"Run {run.Number} · {s.Label}" : s.Label;
            SetGrid.Columns.Add(new DataGridTextColumn
            {
                Header = $"{head}\n{s.Timestamp.ToLocalTime():HH:mm:ss}",
                Binding = new Binding("c" + i),
                MinWidth = 140,
            });
        }
        SetGrid.ItemsSource = table.DefaultView;
    }

    private string RunStatus(IReadOnlyList<SiphogSnapshotRun> runs, IReadOnlyList<SiphogSnapshotRun> shown, int columns)
    {
        if (columns == 0) return "No sets in this file.";
        if (runs.Count < 2 || _runPick == AllRuns) return string.Empty;
        var r = shown[0];
        return $"Showing run {r.Number} of {runs.Count} (started {r.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss})" +
               (r.Number == runs.Count ? " — this serial was run before; pick an earlier run or All runs to compare attempts." : ".");
    }

    /// <summary>One row per parameter (+ min/max), then run / time / operator / window / source / note.</summary>
    private DataTable BuildTable(SiphogSnapshotFile file, IReadOnlyList<SiphogSnapshotSet> sets, IReadOnlyList<SiphogSnapshotRun> setRuns)
    {
        var table = new DataTable();
        table.Columns.Add("p", typeof(string));
        for (int i = 0; i < sets.Count; i++) table.Columns.Add("c" + i, typeof(string));

        void Row(string name, Func<SiphogSnapshotSet, string> cell)
        {
            var r = table.NewRow();
            r["p"] = name;
            for (int i = 0; i < sets.Count; i++) r["c" + i] = cell(sets[i]);
            table.Rows.Add(r);
        }

        bool minMax = MinMaxCheck.IsChecked == true;
        foreach (var (key, name) in Params)
        {
            var unit = Unit(file, key);
            Row($"{name} ({unit})", s => s.Values.TryGetValue(key, out var v) ? $"{Fmt(v.Mean)} ± {Fmt(v.Std)}" : "—");
            if (!minMax) continue;
            Row("   min", s => s.Values.TryGetValue(key, out var v) ? Fmt(v.Min) : "");
            Row("   max", s => s.Values.TryGetValue(key, out var v) ? Fmt(v.Max) : "");
        }
        Row("SAG % of target", s => s.SagPercentOfTarget is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "—");
        var runOf = new Dictionary<SiphogSnapshotSet, SiphogSnapshotRun>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < sets.Count; i++) runOf[sets[i]] = setRuns[i];
        Row("Run", s => runOf.TryGetValue(s, out var r)
            ? $"Run {r.Number} · {r.Started.ToLocalTime():MM-dd HH:mm}" + (r.Tracked ? "" : " (untracked)")
            : "");
        Row("Time", s => s.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Row("Operator", s => s.Operator == null ? "" : $"{s.Operator.Name} ({s.Operator.Id})");
        Row("Window", s => $"{s.Window.Samples} × / {s.Window.ActualMs} ms");
        Row("Source", s => s.Source == null ? "" : string.Join(" › ", new[] { s.Source.Program, s.Source.Step }.Where(x => !string.IsNullOrWhiteSpace(x))));
        Row("Note", s => s.Note ?? "");
        return table;
    }

    // ---------------------------------------------------------------- trend charts

    private void OnChartOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressChartOptions) RenderCharts();
    }

    /// <summary>Re-fills the label picker from every loaded set, keeping the current choice if it still exists.</summary>
    private void RefreshLabelChoices()
    {
        var keep = LabelCombo.SelectedItem as string ?? AllLabels;
        var labels = OrderedLabels(_allFiles);
        _suppressChartOptions = true;
        LabelCombo.ItemsSource = new[] { AllLabels }.Concat(labels).ToList();
        LabelCombo.SelectedItem = labels.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : AllLabels;
        _suppressChartOptions = false;
    }

    /// <summary>Distinct labels in the order they were first used (chronologically).</summary>
    private static List<string> OrderedLabels(IEnumerable<SiphogSnapshotFile> files)
        => files.SelectMany(f => f.Sets).OrderBy(s => s.Timestamp)
            .Select(s => s.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Rebuilds the six parameter charts from every DUT in the serial list (filter applied).
    /// <b>Timeline</b>: each set at its real time, one line per label joined across other labels'
    /// points. <b>By DUT serial</b>: one slot per DUT ordered by its first snapshot, the newest
    /// set per label, the selected DUT marked.
    /// </summary>
    private void RenderCharts()
    {
        if (_suppressChartOptions || _charts[^1] == null) return;   // still constructing

        var shown = SerialList.ItemsSource as IEnumerable<string>;
        var allowed = shown == null ? null : new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase);
        var files = _allFiles.Where(f => allowed == null || allowed.Contains(f.Serial)).ToList();

        var pick = LabelCombo.SelectedItem as string;
        var labels = pick == null || pick == AllLabels
            ? OrderedLabels(files)
            : new List<string> { pick };
        bool byDut = ByDutRadio.IsChecked == true;

        List<string> xLabels;
        List<double>? xPositions = null;
        // One dictionary per X slot: label → the set plotted there.
        var slots = new List<Dictionary<string, SiphogSnapshotSet>>();

        if (byDut)
        {
            var duts = files.Where(f => f.Sets.Count > 0).OrderBy(f => f.Sets.Min(x => x.Timestamp)).ToList();
            xLabels = duts.Select(f => f.Serial).ToList();
            // A re-run DUT plots its latest run only, so one slot never mixes two attempts.
            foreach (var f in duts)
                slots.Add(SelectSets(f.Runs()[^1].Sets, all: false).ToDictionary(x => x.Label, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            var sets = files.SelectMany(f => f.Sets.Select(x => (f.Serial, Set: x)))
                .Where(t => labels.Contains(t.Set.Label, StringComparer.OrdinalIgnoreCase))
                .OrderBy(t => t.Set.Timestamp).ToList();
            xLabels = sets.Select(t => $"{t.Serial} {t.Set.Timestamp.ToLocalTime():MM-dd HH:mm}").ToList();
            xPositions = sets.Select(t => (double)t.Set.Timestamp.UtcTicks).ToList();
            foreach (var t in sets)
                slots.Add(new Dictionary<string, SiphogSnapshotSet>(StringComparer.OrdinalIgnoreCase) { [t.Set.Label] = t.Set });
        }

        int highlight = byDut && SerialList.SelectedItem is string sel
            ? xLabels.FindIndex(x => string.Equals(x, sel, StringComparison.OrdinalIgnoreCase)) : -1;

        for (int p = 0; p < Params.Length; p++)
        {
            var key = Params[p].Key;
            var series = new List<ChartSeries>();
            for (int l = 0; l < labels.Count; l++)
            {
                var values = new double?[slots.Count];
                bool any = false;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].TryGetValue(labels[l], out var set) && set.Values.TryGetValue(key, out var st))
                    {
                        values[i] = st.Mean;
                        any = true;
                    }
                }
                if (any) series.Add(new ChartSeries(labels[l], LabelColors[l % LabelColors.Length], values));
            }
            var unit = files.Count > 0 ? Unit(files[0], key) : DefaultUnits.GetValueOrDefault(key, "");
            _charts[p].SetSeries(unit, xLabels, series, xPositions, connectGaps: !byDut);
            _charts[p].SetHighlight(highlight);
        }

        ChartStatus.Text = slots.Count == 0 ? "No snapshots to plot."
            : byDut ? $"{slots.Count} DUT(s) · {labels.Count} label(s)"
                    : $"{slots.Count} set(s) from {files.Count} DUT(s)";
    }

    /// <summary>All sets in order, or the newest set per label ordered by its time.</summary>
    private static List<SiphogSnapshotSet> SelectSets(IReadOnlyList<SiphogSnapshotSet> sets, bool all)
    {
        if (all) return sets.ToList();
        var latest = new Dictionary<string, SiphogSnapshotSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets)
            if (!latest.TryGetValue(s.Label, out var cur) || s.Timestamp >= cur.Timestamp)
                latest[s.Label] = s;
        return latest.Values.OrderBy(s => s.Timestamp).ToList();
    }

    private static string Unit(SiphogSnapshotFile file, string key)
        => file.Units.TryGetValue(key, out var u) ? u : DefaultUnits.GetValueOrDefault(key, "");

    private static string Fmt(double v)
    {
        var a = Math.Abs(v);
        var fmt = a >= 100 ? "0.00" : a >= 1 ? "0.0000" : "0.00000";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- export

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_host == null || SerialList.ItemsSource is not List<string> serials || serials.Count == 0) return;

        var product = _host.Process.ActiveProduct ?? "product";
        var dialog = new SaveFileDialog
        {
            Title = "Export SiPhOG snapshots",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"siphog-snapshots-{Sanitize(product)}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var process = _host.Process;
        var path = dialog.FileName;
        ExportButton.IsEnabled = false;
        StatusText.Text = $"Exporting {serials.Count} serial(s)…";
        try
        {
            var rows = await Task.Run(() =>
            {
                var files = serials.Select(process.GetSiphogSnapshots).Where(f => f != null).Cast<SiphogSnapshotFile>().ToList();
                var (csv, count) = BuildCsv(files);
                File.WriteAllText(path, csv, new UTF8Encoding(true));   // BOM so Excel reads µ / °
                return count;
            });
            StatusText.Text = $"Exported {rows} rows to {Path.GetFileName(path)}";
            Log(LogLevel.Info, $"DUT Data: exported {rows} SiPhOG snapshot rows to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
            Log(LogLevel.Warning, $"SiPhOG snapshot export failed: {ex.Message}");
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private static (string Csv, int Rows) BuildCsv(IReadOnlyList<SiphogSnapshotFile> files)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Serial,Run,Run Id,Run Started,Label,Timestamp,Operator Id,Operator,Station,Program,Step,Parameter,Unit,Mean,Min,Max,Std,Samples,Window ms,SAG % of target,Note");
        int rows = 0;
        foreach (var f in files)
            foreach (var (run, s) in f.Runs().SelectMany(r => r.Sets.Select(x => (r, x))))
                foreach (var (key, _) in Params)
                {
                    if (!s.Values.TryGetValue(key, out var v)) continue;
                    sb.Append(Csv(f.Serial)).Append(',')
                      .Append(run.Number).Append(',')
                      .Append(run.Id ?? "").Append(',')
                      .Append(run.Started.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", inv)).Append(',')
                      .Append(Csv(s.Label)).Append(',')
                      .Append(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", inv)).Append(',')
                      .Append(Csv(s.Operator?.Id ?? "")).Append(',')
                      .Append(Csv(s.Operator?.Name ?? "")).Append(',')
                      .Append(Csv(s.Station ?? "")).Append(',')
                      .Append(Csv(s.Source?.Program ?? "")).Append(',')
                      .Append(Csv(s.Source?.Step ?? "")).Append(',')
                      .Append(key).Append(',')
                      .Append(Csv(Unit(f, key))).Append(',')
                      .Append(v.Mean.ToString("R", inv)).Append(',')
                      .Append(v.Min.ToString("R", inv)).Append(',')
                      .Append(v.Max.ToString("R", inv)).Append(',')
                      .Append(v.Std.ToString("R", inv)).Append(',')
                      .Append(s.Window.Samples).Append(',')
                      .Append(s.Window.ActualMs).Append(',')
                      .Append(s.SagPercentOfTarget?.ToString("R", inv) ?? "").Append(',')
                      .Append(Csv(s.Note ?? ""))
                      .AppendLine();
                    rows++;
                }
        return (sb.ToString(), rows);
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
