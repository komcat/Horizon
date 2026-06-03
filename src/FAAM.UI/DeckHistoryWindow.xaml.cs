using System.Windows;
using System.Windows.Controls;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// Read-only revision history of a deck: each save is a snapshot stamped with the
/// operator + timestamp. Selecting a revision shows the per-entry diff (keyed by the
/// entry UID) versus the previous revision — added / removed / changed, down to the
/// field (e.g. <c>delay: 2 → 5</c>). The Query tab searches saved deck entries across
/// all decks (by deck UID / entry UID / program / operator).
/// </summary>
public partial class DeckHistoryWindow : Window
{
    private readonly EngineHost _host;
    private readonly DeckHistory _history;

    public DeckHistoryWindow(EngineHost host, string deckUid, string deckName, string? queryEntryUid = null)
    {
        InitializeComponent();
        _host = host;
        _history = host.Process.GetDeckHistory(deckUid) ?? new DeckHistory { DeckUid = deckUid, DeckName = deckName };
        var name = string.IsNullOrEmpty(_history.DeckName) ? deckName : _history.DeckName;
        HeaderText.Text = $"Deck History — '{name}'   ({_history.Versions.Count} revision(s))";

        // Seed the query tab with this deck's UID so a Search shows its entry history.
        DeckUidBox.Text = deckUid;

        VersionList.ItemsSource = BuildVersionRows();
        if (VersionList.Items.Count > 0)
            VersionList.SelectedIndex = 0;
        else
            DiffHeader.Text = "No saved revisions yet.";

        // Opened pointing at a specific entry — jump to the Query tab pre-filled.
        if (!string.IsNullOrWhiteSpace(queryEntryUid))
        {
            HistoryTabs.SelectedIndex = 1;
            EntryUidBox.Text = queryEntryUid;
            RunQuery();
        }
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
        var changes = DeckDiff.Compute(prev, row.Version.Snapshot);

        DiffHeader.Text = row.Index == 0
            ? $"v{row.Version.Version} — initial revision · {changes.Count} entry(ies) · by {Who(row.Version)} · station {row.Version.StationId ?? "—"}"
            : $"v{row.Version.Version} vs v{_history.Versions[row.Index - 1].Version} · {changes.Count} change(s) · by {Who(row.Version)} · station {row.Version.StationId ?? "—"}";

        DiffList.ItemsSource = changes
            .Select(c => new DiffRow(c.Kind.ToString(), c.Label, ShortUid(c.Uid), string.Join("\n", c.Details)))
            .ToList();
    }

    // --- cross-deck query ---

    private void OnSearch(object sender, RoutedEventArgs e) => RunQuery();

    private void RunQuery()
    {
        var query = new DeckHistoryQuery
        {
            DeckUid = Nz(DeckUidBox.Text),
            EntryUid = Nz(EntryUidBox.Text),
            Program = Nz(ProgramBox.Text),
            OperatorId = Nz(OperatorBox.Text),
        };

        var rows = _host.Process.QueryDeckHistory(query)
            .Select(r => new ResultRow(
                r.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                r.Product, r.DeckName, "v" + r.Version, r.OperatorName ?? "—",
                ShortUid(r.EntryUid), r.Position, r.Program,
                r.Auto ? "on" : "off", r.DelaySeconds.ToString("0.##"), r.Condition))
            .ToList();

        ResultsGrid.ItemsSource = rows;
        QueryStatus.Text = rows.Count == 0
            ? "No matching deck-entry history."
            : $"{rows.Count} row(s)" + (rows.Count >= 2000 ? " (capped at 2000)." : ".");
    }

    private void OnClearQuery(object sender, RoutedEventArgs e)
    {
        DeckUidBox.Clear(); EntryUidBox.Clear(); ProgramBox.Clear(); OperatorBox.Clear();
        ResultsGrid.ItemsSource = null;
        QueryStatus.Text = "Enter a filter and Search.";
    }

    private static string? Nz(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Who(DeckVersion v)
        => string.IsNullOrEmpty(v.OperatorName) ? (v.OperatorId ?? "—") : v.OperatorName;

    private static string ShortUid(string uid) => uid.Length <= 14 ? uid : uid[..14] + "…";

    private sealed record VersionRow(string Header, int Index, DeckVersion Version);

    private sealed record DiffRow(string Kind, string Label, string Uid, string Detail);

    private sealed record ResultRow(
        string When, string Product, string Deck, string Version, string Operator,
        string Uid, int Position, string Program, string Auto, string Delay, string Condition);
}
