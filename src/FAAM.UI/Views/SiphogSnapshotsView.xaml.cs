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
/// (mean ± std, optional min / max rows) plus SAG % of target, time, operator, window and
/// source. Read-only; file reads run off the UI thread. Export writes every set of the
/// listed serials in long form (one row per set × parameter).
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
    private int _generation;
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
        _serials = Array.Empty<string>();
        _allFiles = Array.Empty<SiphogSnapshotFile>();
        _file = null;
        SerialList.ItemsSource = null;
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
        var keep = SerialList.SelectedItem as string;
        ChartStatus.Text = "Loading…";
        try
        {
            // One small file per DUT — read them all (off the UI thread) so the charts span every DUT.
            (_serials, _allFiles) = await Task.Run(() =>
            {
                var serials = process.ListSiphogSnapshotSerials()
                    .OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                var files = serials.Select(process.GetSiphogSnapshots)
                    .Where(f => f != null).Cast<SiphogSnapshotFile>().ToList();
                return ((IReadOnlyList<string>)serials, (IReadOnlyList<SiphogSnapshotFile>)files);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not list snapshot files: {ex.Message}";
            ChartStatus.Text = string.Empty;
            return;
        }
        RefreshLabelChoices();
        ApplyFilter();
        if (keep != null && SerialList.Items.Contains(keep)) SerialList.SelectedItem = keep;
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
        _file = file;
        Render();
        if (ByDutRadio.IsChecked == true) RenderCharts();   // move the selected-DUT marker
    }

    // ---------------------------------------------------------------- grid

    private void Render()
    {
        SetGrid.Columns.Clear();
        SetGrid.ItemsSource = null;
        if (_file == null)
        {
            HeaderText.Text = SerialList.SelectedItem is string s ? $"DUT {s} — file unreadable." : "Select a DUT serial.";
            return;
        }

        var sets = SelectSets(_file, AllSetsCheck.IsChecked == true);
        var labels = _file.Sets.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        HeaderText.Text = $"DUT {_file.Serial} — {_file.Sets.Count} set(s), {labels} label(s)";
        StatusText.Text = sets.Count == 0 ? "No sets in this file." : string.Empty;

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
            var unit = Unit(_file, key);
            Row($"{name} ({unit})", s => s.Values.TryGetValue(key, out var v) ? $"{Fmt(v.Mean)} ± {Fmt(v.Std)}" : "—");
            if (!minMax) continue;
            Row("   min", s => s.Values.TryGetValue(key, out var v) ? Fmt(v.Min) : "");
            Row("   max", s => s.Values.TryGetValue(key, out var v) ? Fmt(v.Max) : "");
        }
        Row("SAG % of target", s => s.SagPercentOfTarget is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "—");
        Row("Time", s => s.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Row("Operator", s => s.Operator == null ? "" : $"{s.Operator.Name} ({s.Operator.Id})");
        Row("Window", s => $"{s.Window.Samples} × / {s.Window.ActualMs} ms");
        Row("Source", s => s.Source == null ? "" : string.Join(" › ", new[] { s.Source.Program, s.Source.Step }.Where(x => !string.IsNullOrWhiteSpace(x))));
        Row("Note", s => s.Note ?? "");

        SetGrid.Columns.Add(new DataGridTextColumn { Header = "Parameter", Binding = new Binding("p"), MinWidth = 170 });
        for (int i = 0; i < sets.Count; i++)
        {
            var s = sets[i];
            SetGrid.Columns.Add(new DataGridTextColumn
            {
                Header = $"{s.Label}\n{s.Timestamp.ToLocalTime():HH:mm:ss}",
                Binding = new Binding("c" + i),
                MinWidth = 140,
            });
        }
        SetGrid.ItemsSource = table.DefaultView;
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
            foreach (var f in duts)
                slots.Add(SelectSets(f, all: false).ToDictionary(x => x.Label, StringComparer.OrdinalIgnoreCase));
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

    /// <summary>All sets in file order, or the newest set per label ordered by its time.</summary>
    private static List<SiphogSnapshotSet> SelectSets(SiphogSnapshotFile file, bool all)
    {
        if (all) return file.Sets.ToList();
        var latest = new Dictionary<string, SiphogSnapshotSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in file.Sets)
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
        sb.AppendLine("Serial,Label,Timestamp,Operator Id,Operator,Station,Program,Step,Parameter,Unit,Mean,Min,Max,Std,Samples,Window ms,SAG % of target,Note");
        int rows = 0;
        foreach (var f in files)
            foreach (var s in f.Sets)
                foreach (var (key, _) in Params)
                {
                    if (!s.Values.TryGetValue(key, out var v)) continue;
                    sb.Append(Csv(f.Serial)).Append(',')
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
