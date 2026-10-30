using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Horizon.Engine;
using Horizon.Workflow;
using Microsoft.Win32;

namespace Horizon.Station.UI.Views;

/// <summary>
/// Generic DUT Data page (<see cref="IStationPage"/>): the active product's DUT records — the
/// serials, the selected unit's runs (start / end / verdict / operator / station / source) and
/// the selected run's recorded values, with a CSV export of one run. Re-reads on every show.
/// Read-only; a head with richer data views (FAAM: UV + SiPhOG snapshots) supplies its own page.
/// </summary>
public partial class DutRecordsView : UserControl, IStationPage
{
    /// <summary>Characters that make a CSV field need quoting.</summary>
    private static readonly SearchValues<char> CsvSpecial = SearchValues.Create(",\"\r\n");

    private readonly EngineHost _host;
    private IReadOnlyList<string> _serials = Array.Empty<string>();
    private DutRecord? _record;

    public DutRecordsView(EngineHost host)
    {
        _host = host;
        InitializeComponent();
    }

    public void Activate() => Reload();

    public void Deactivate()
    {
        // Nothing live — the page reads the records on show.
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ShowSerials();

    /// <summary>Re-lists the product's serials, keeping the selection when it still exists.</summary>
    private void Reload()
    {
        var keep = SerialList.SelectedItem as string;
        try
        {
            // Names only — a record is loaded when its serial is selected, never all of them.
            _serials = _host.Process.ListDutRecords()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _serials = Array.Empty<string>();
            Log(LogLevel.Warning, $"DUT Data: could not list the DUT records — {ex.Message}");
        }
        ShowSerials();
        if (keep != null && SerialList.Items.Contains(keep))
            SerialList.SelectedItem = keep;
    }

    private void ShowSerials()
    {
        var filter = FilterBox.Text.Trim();
        var shown = filter.Length == 0
            ? _serials
            : _serials.Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        SerialList.ItemsSource = shown;
        CountText.Text = $"{shown.Count} of {_serials.Count} unit(s) · product {_host.Process.ActiveProduct ?? "—"}";
    }

    private void OnSerialSelected(object sender, SelectionChangedEventArgs e)
    {
        _record = SerialList.SelectedItem is string serial ? _host.Process.GetDutRecord(serial) : null;
        UnitTitle.Text = _record == null ? "RUNS" : $"RUNS — {_record.Serial}";
        RunGrid.ItemsSource = _record?.Runs
            .OrderByDescending(r => r.StartedUtc)
            .Select(r => new RunRow(r))
            .ToList();
        if (RunGrid.Items.Count > 0)
            RunGrid.SelectedIndex = 0;
        else
            ShowRun(null);
    }

    private void OnRunSelected(object sender, SelectionChangedEventArgs e)
        => ShowRun((RunGrid.SelectedItem as RunRow)?.Run);

    private void ShowRun(DutRun? run)
    {
        EntryGrid.ItemsSource = run?.Entries.Select(x => new EntryRow(x)).ToList();
        ExportButton.IsEnabled = run != null;
    }

    /// <summary>Saves the selected run's values as CSV (time, key, value, type).</summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_record == null || RunGrid.SelectedItem is not RunRow row)
            return;

        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{_record.Serial}_{row.Run.StartedUtc.ToLocalTime():yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var sb = new StringBuilder("timestamp,key,value,type\r\n");
            foreach (var x in row.Run.Entries)
                sb.Append(Csv(x.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)))
                  .Append(',').Append(Csv(x.Key))
                  .Append(',').Append(Csv(x.Value))
                  .Append(',').Append(Csv(x.Type.ToString()))
                  .Append("\r\n");
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Log(LogLevel.Info, $"DUT Data: exported {row.Run.Entries.Count} value(s) of {_record.Serial} to {dlg.FileName}.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"DUT Data: export failed — {ex.Message}");
        }
    }

    private static string Csv(string s)
        => s.AsSpan().IndexOfAny(CsvSpecial) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private void Log(LogLevel level, string message)
        => _host.Log.Add(new LogEntry(level, DateTimeOffset.Now, StationApp.Title, message));

    /// <summary>One row of the runs grid — pre-formatted for display (public: its properties are read by binding).</summary>
    public sealed class RunRow
    {
        public RunRow(DutRun run) => Run = run;

        public DutRun Run { get; }
        public string Started => Run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string Ended => Run.EndedUtc == default ? "—" : Run.EndedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public string Verdict => Run.Verdict.ToString();
        public string Operator => string.IsNullOrEmpty(Run.OperatorName) ? Run.OperatorId ?? "—" : $"{Run.OperatorName} ({Run.OperatorId})";
        public string Station => Run.StationId ?? "—";
        public string Source => Run.Source;
        public int Count => Run.Entries.Count;
    }

    /// <summary>One recorded value, pre-formatted for display (public: read by binding).</summary>
    public sealed class EntryRow
    {
        public EntryRow(DutEntry e)
        {
            Time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            Key = e.Key;
            Value = e.Value;
        }

        public string Time { get; }
        public string Key { get; }
        public string Value { get; }
    }
}
