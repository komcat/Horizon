using System.Windows;
using System.Windows.Input;
using Horizon.Workflow;

namespace FAAM.UI;

/// <summary>
/// Modal deck picker — lists the active product's decks with each one's current
/// committed version and last-commit stamp, and returns the chosen deck name via
/// <see cref="SelectedDeckName"/>. Replaces the inline deck combo on the Process Deck
/// tab; the editor loads the selection.
/// </summary>
public partial class DeckPickerWindow : Window
{
    /// <summary>The deck the operator chose, or null if cancelled.</summary>
    public string? SelectedDeckName { get; private set; }

    public DeckPickerWindow(EngineHost host)
    {
        InitializeComponent();

        var rows = new List<Row>();
        foreach (var name in host.Process.ListDecks())
        {
            var uid = host.Process.GetDeckUid(name);
            var latest = string.IsNullOrEmpty(uid) ? null : host.Process.GetDeckHistory(uid)?.Latest;
            rows.Add(new Row(
                name,
                latest != null ? $"v{latest.Version}" : "uncommitted",
                latest != null ? $"{latest.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Who(latest)}" : "—"));
        }

        DeckGrid.ItemsSource = rows;
        if (rows.Count > 0) DeckGrid.SelectedIndex = 0;
    }

    private void OnLoad(object sender, RoutedEventArgs e) => Commit();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void Commit()
    {
        if (DeckGrid.SelectedItem is not Row r) return;
        SelectedDeckName = r.Deck;
        DialogResult = true;
    }

    private static string Who(DeckVersion v)
        => string.IsNullOrEmpty(v.OperatorName) ? (v.OperatorId ?? "—") : v.OperatorName;

    private sealed record Row(string Deck, string Version, string LastCommit);
}
