using System.Windows;
using System.Windows.Controls;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The version repository page — a git-style browse + revert over the program / deck
/// revision history that each <b>commit</b> records (operator + timestamp + full
/// snapshot, in <c>history.db</c>). Pick a program or deck on the left, see its version
/// timeline (newest first, with each commit's optional note) in the middle, the
/// per-block / per-entry diff vs the previous version on the right, and <b>Revert to
/// this version</b> to set any past version as the current working copy. Revert writes
/// the old snapshot back through Layer 2 as the working file (no new version — the
/// operator commits afterwards to keep it). Read-only browse otherwise; no background work.
/// </summary>
public partial class RepositoryView : UserControl
{
    private enum RepoKind { Programs, Decks }

    private EngineHost? _host;
    private RepoKind _kind = RepoKind.Programs;

    // The currently selected item's loaded history (one of these is set at a time).
    private ProgramHistory? _programHistory;
    private DeckHistory? _deckHistory;
    private string? _selectedItem;
    private string? _selectedDeckUid;

    public RepositoryView() => InitializeComponent();

    /// <summary>Supplies the engine host (Layer 2 access).</summary>
    public void Attach(EngineHost host) => _host = host;

    /// <summary>Refreshes the item list when the page is shown.</summary>
    public void Activate() => RefreshItems();

    /// <summary>No background work to stop — symmetry with the other pages.</summary>
    public void Deactivate() { }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        // Fires during XAML parse (ProgramsTab default-checked) before DecksTab exists
        // and before Attach — ignore until the page is live.
        if (!IsLoaded) return;
        _kind = DecksTab.IsChecked == true ? RepoKind.Decks : RepoKind.Programs;
        RefreshItems();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshItems();

    /// <summary>Repopulates the left-hand item list for the current kind, preserving the selection if it survives.</summary>
    private void RefreshItems()
    {
        if (_host == null) return;

        var previous = _selectedItem;
        var items = _kind == RepoKind.Programs
            ? _host.Process.ListPrograms()
            : _host.Process.ListDecks();

        ItemsHeader.Text = _kind == RepoKind.Programs ? "Programs" : "Decks";
        ItemList.ItemsSource = items;

        ClearVersions();
        if (previous != null && items.Contains(previous))
            ItemList.SelectedItem = previous;   // re-triggers OnItemSelected
    }

    private void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        ClearVersions();
        if (_host == null || ItemList.SelectedItem is not string name) return;

        _selectedItem = name;
        if (_kind == RepoKind.Programs)
            LoadProgramVersions(name);
        else
            LoadDeckVersions(name);
    }

    private void LoadProgramVersions(string name)
    {
        _programHistory = _host!.Process.GetProgramHistory(name);
        var count = _programHistory?.Versions.Count ?? 0;
        VersionsHeader.Text = $"{name} — {count} version(s)";
        VersionListBox.ItemsSource = BuildRows(_programHistory?.Versions
            .Select(v => (v.Version, v.SavedUtc, v.OperatorId, v.OperatorName, v.Note)).ToList());
        SelectNewest();
    }

    private void LoadDeckVersions(string name)
    {
        _selectedDeckUid = _host!.Process.GetDeckUid(name);
        if (string.IsNullOrEmpty(_selectedDeckUid))
        {
            // Legacy / never-saved deck: no UID → no version lineage yet.
            VersionsHeader.Text = $"{name} — no version history";
            CheckoutStatus.Text = "This deck has no version history yet. Save it once (in the deck editor) to start versioning.";
            return;
        }

        _deckHistory = _host.Process.GetDeckHistory(_selectedDeckUid);
        var count = _deckHistory?.Versions.Count ?? 0;
        VersionsHeader.Text = $"{name} — {count} version(s)";
        VersionListBox.ItemsSource = BuildRows(_deckHistory?.Versions
            .Select(v => (v.Version, v.SavedUtc, v.OperatorId, v.OperatorName, v.Note)).ToList());
        SelectNewest();
    }

    /// <summary>Builds newest-first version rows from a flat (version, when, operator, note) list.</summary>
    private static List<VersionRowVm> BuildRows(
        List<(int Version, DateTime SavedUtc, string? OperatorId, string? OperatorName, string? Note)>? versions)
    {
        var rows = new List<VersionRowVm>();
        if (versions == null) return rows;
        for (int i = versions.Count - 1; i >= 0; i--)   // newest first
        {
            var v = versions[i];
            var who = string.IsNullOrEmpty(v.OperatorName) ? (v.OperatorId ?? "—") : v.OperatorName;
            rows.Add(new VersionRowVm(
                $"v{v.Version}   ·   {v.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}   ·   {who}",
                v.Note ?? string.Empty,
                string.IsNullOrEmpty(v.Note) ? Visibility.Collapsed : Visibility.Visible,
                i, v.Version));
        }
        return rows;
    }

    private void SelectNewest()
    {
        if (VersionListBox.Items.Count > 0) VersionListBox.SelectedIndex = 0;   // newest is first
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VersionListBox.SelectedItem is not VersionRowVm row)
        {
            DiffList.ItemsSource = null;
            DiffHeader.Text = "Select a version to see what changed.";
            CheckoutButton.IsEnabled = false;
            return;
        }

        if (_kind == RepoKind.Programs)
            ShowProgramDiff(row);
        else
            ShowDeckDiff(row);

        CheckoutButton.IsEnabled = true;
        CheckoutStatus.Text = $"Revert sets the working {(_kind == RepoKind.Programs ? "program" : "deck")} to v{row.Version} "
            + "(no new version — commit afterwards to keep it).";
    }

    private void ShowProgramDiff(VersionRowVm row)
    {
        var versions = _programHistory?.Versions;
        if (versions == null || row.Index >= versions.Count) return;

        var prev = row.Index > 0 ? versions[row.Index - 1].Snapshot : null;
        var changes = ProgramDiff.Compute(prev, versions[row.Index].Snapshot);
        DiffHeader.Text = row.Index == 0
            ? $"v{row.Version} — initial revision · {changes.Count} block(s)"
            : $"v{row.Version} vs v{versions[row.Index - 1].Version} · {changes.Count} change(s)";
        DiffList.ItemsSource = changes
            .Select(c => new DiffRow(c.Kind.ToString(), c.Label, ShortUid(c.Uid), string.Join("\n", c.Details)))
            .ToList();
    }

    private void ShowDeckDiff(VersionRowVm row)
    {
        var versions = _deckHistory?.Versions;
        if (versions == null || row.Index >= versions.Count) return;

        var prev = row.Index > 0 ? versions[row.Index - 1].Snapshot : null;
        var changes = DeckDiff.Compute(prev, versions[row.Index].Snapshot);
        DiffHeader.Text = row.Index == 0
            ? $"v{row.Version} — initial revision · {changes.Count} entr(y/ies)"
            : $"v{row.Version} vs v{versions[row.Index - 1].Version} · {changes.Count} change(s)";
        DiffList.ItemsSource = changes
            .Select(c => new DiffRow(c.Kind.ToString(), c.Label, ShortUid(c.Uid), string.Join("\n", c.Details)))
            .ToList();
    }

    private void OnCheckout(object sender, RoutedEventArgs e)
    {
        if (_host == null || _selectedItem == null || VersionListBox.SelectedItem is not VersionRowVm row)
            return;

        var kindWord = _kind == RepoKind.Programs ? "program" : "deck";
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Set the working {kindWord} '{_selectedItem}' to v{row.Version}?\n\n" +
            "This overwrites the current working file with that version's content. It does " +
            "not create a new version — commit afterwards if you want to keep it. If this " +
            kindWord + " is open in an editor, re-open it to see the reverted content.",
            "Revert to version", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = _kind == RepoKind.Programs
            ? _host.Process.RestoreProgramVersion(_selectedItem, row.Version)
            : _host.Process.RestoreDeckVersion(_selectedDeckUid ?? string.Empty, row.Version);

        if (!result.IsSuccess)
        {
            Log(LogLevel.Warning, $"Revert failed: {result.Message}");
            MessageBox.Show(Window.GetWindow(this), $"Revert failed: {result.Message}",
                "Revert to version", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log(LogLevel.Info, $"Reverted {kindWord} '{_selectedItem}' to v{row.Version} in the working copy.");
        // Revert doesn't add a version, so the timeline is unchanged — just confirm.
        CheckoutStatus.Text = $"Working {kindWord} is now v{row.Version}. Re-open the editor to see it; commit to keep it as a new version.";
    }

    private void ClearVersions()
    {
        _programHistory = null;
        _deckHistory = null;
        _selectedDeckUid = null;
        VersionListBox.ItemsSource = null;
        DiffList.ItemsSource = null;
        VersionsHeader.Text = "Select an item.";
        DiffHeader.Text = "Select a version to see what changed.";
        CheckoutButton.IsEnabled = false;
        CheckoutStatus.Text = "Pick a version to make it current.";
    }

    private void Log(LogLevel level, string message)
        => _host?.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Repository", message));

    private static string ShortUid(string uid) => uid.Length <= 14 ? uid : uid[..14] + "…";

    /// <summary>One row in the version timeline.</summary>
    private sealed record VersionRowVm(string Title, string Note, Visibility NoteVisibility, int Index, int Version);

    /// <summary>One diff line (mirrors the History windows' diff rows).</summary>
    private sealed record DiffRow(string Kind, string Label, string Uid, string Detail);
}
