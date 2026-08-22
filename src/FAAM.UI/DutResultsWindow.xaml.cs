using System.Windows;
using System.Windows.Controls;

namespace FAAM.UI;

/// <summary>
/// A read-only DUT traceability viewer for the active product: serials on the left,
/// and the selected unit's runs (start / end / verdict / <b>operator</b> /
/// <b>station</b> / source) on the right. The operator + station columns are the
/// point — they answer "who ran this unit, on which machine".
/// </summary>
public partial class DutResultsWindow : Window
{
    private readonly EngineHost _host;

    public DutResultsWindow(EngineHost host)
    {
        _host = host;
        InitializeComponent();
        HeaderText.Text = $"DUT Results — product '{_host.Process.ActiveProduct ?? "(none)"}'";
        LoadSerials();
    }

    private void LoadSerials()
    {
        var selected = SerialList.SelectedItem as string;
        SerialList.ItemsSource = _host.Process.ListDutRecords();
        if (selected != null)
            SerialList.SelectedItem = selected;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        LoadSerials();
        if (SerialList.SelectedItem is string serial)
            ShowRuns(serial);
    }

    private void OnSerialSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SerialList.SelectedItem is string serial)
            ShowRuns(serial);
    }

    private void ShowRuns(string serial)
    {
        var record = _host.Process.GetDutRecord(serial);
        if (record == null)
        {
            RunsHeader.Text = $"Serial '{serial}' — no record.";
            RunsGrid.ItemsSource = null;
            return;
        }

        RunsHeader.Text = $"Serial '{record.Serial}' — {record.Runs.Count} run(s), created {record.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        // Newest run first.
        var rows = new List<DutRunRow>();
        for (int i = record.Runs.Count - 1; i >= 0; i--)
        {
            var r = record.Runs[i];
            rows.Add(new DutRunRow(
                r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                r.EndedUtc == default ? "—" : r.EndedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                r.Verdict.ToString(),
                string.IsNullOrEmpty(r.OperatorName) ? (r.OperatorId ?? "—") : r.OperatorName,
                r.OperatorLevel ?? "—",
                string.IsNullOrEmpty(r.StationId) ? "—" : r.StationId!,
                r.MachineCredential ?? "—",
                r.Source,
                r.Entries.Count));
        }
        RunsGrid.ItemsSource = rows;
    }

    /// <summary>One row of the runs grid — pre-formatted for display.</summary>
    private sealed record DutRunRow(
        string Started, string Ended, string Verdict,
        string Operator, string Level, string Station, string Credential,
        string Source, int Values);
}
