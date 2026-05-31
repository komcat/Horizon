using System.Windows;
using System.Windows.Controls;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// Read-only revision history of a process program: each save is a snapshot stamped
/// with the operator + timestamp. Selecting a revision shows the per-block diff
/// (keyed by the block UID) versus the previous revision — added / removed / changed,
/// down to the field (e.g. <c>distance: 1 → 2</c>).
/// </summary>
public partial class ProgramHistoryWindow : Window
{
    private readonly ProgramHistory _history;

    public ProgramHistoryWindow(EngineHost host, string program)
    {
        InitializeComponent();
        _history = host.Process.GetProgramHistory(program) ?? new ProgramHistory { Program = program };
        HeaderText.Text = $"History — '{program}'   ({_history.Versions.Count} revision(s))";

        VersionList.ItemsSource = BuildVersionRows();
        if (VersionList.Items.Count > 0)
            VersionList.SelectedIndex = 0;
        else
            DiffHeader.Text = "No saved revisions yet.";
    }

    private List<VersionRow> BuildVersionRows()
    {
        var rows = new List<VersionRow>();
        for (int i = _history.Versions.Count - 1; i >= 0; i--)   // newest first
        {
            var v = _history.Versions[i];
            rows.Add(new VersionRow($"v{v.Version}   ·   {v.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}   ·   {Who(v)}", i, v));
        }
        return rows;
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionRow row)
        {
            DiffList.ItemsSource = null;
            return;
        }

        var prev = row.Index > 0 ? _history.Versions[row.Index - 1].Snapshot : null;
        var changes = ProgramDiff.Compute(prev, row.Version.Snapshot);

        DiffHeader.Text = row.Index == 0
            ? $"v{row.Version.Version} — initial revision · {changes.Count} block(s) · by {Who(row.Version)} · station {row.Version.StationId ?? "—"}"
            : $"v{row.Version.Version} vs v{_history.Versions[row.Index - 1].Version} · {changes.Count} change(s) · by {Who(row.Version)} · station {row.Version.StationId ?? "—"}";

        DiffList.ItemsSource = changes
            .Select(c => new DiffRow(c.Kind.ToString(), c.Label, ShortUid(c.Uid), string.Join("\n", c.Details)))
            .ToList();
    }

    private static string Who(ProgramVersion v)
        => string.IsNullOrEmpty(v.OperatorName) ? (v.OperatorId ?? "—") : v.OperatorName;

    private static string ShortUid(string uid) => uid.Length <= 14 ? uid : uid[..14] + "…";

    private sealed record VersionRow(string Header, int Index, ProgramVersion Version);

    private sealed record DiffRow(string Kind, string Label, string Uid, string Detail);
}
