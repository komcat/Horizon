using System.Windows;
using System.Windows.Input;
using Horizon.Workflow;

namespace Horizon.Station.UI;

/// <summary>
/// Modal program picker — lists the active product's programs with each one's current
/// committed version and last-commit stamp, and returns the chosen program name via
/// <see cref="SelectedProgramName"/>. Replaces the inline program combo on the Program
/// tab; the editor loads the selection.
/// </summary>
public partial class ProgramPickerWindow : Window
{
    /// <summary>The program the operator chose, or null if cancelled.</summary>
    public string? SelectedProgramName { get; private set; }

    public ProgramPickerWindow(EngineHost host)
    {
        InitializeComponent();

        var rows = new List<Row>();
        foreach (var name in host.Process.ListPrograms())
        {
            // Programs are keyed by name in the history store.
            var latest = host.Process.GetProgramHistory(name)?.Latest;
            rows.Add(new Row(
                name,
                latest != null ? $"v{latest.Version}" : "uncommitted",
                latest != null ? $"{latest.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Who(latest)}" : "—"));
        }

        ProgramGrid.ItemsSource = rows;
        if (rows.Count > 0) ProgramGrid.SelectedIndex = 0;
    }

    private void OnLoad(object sender, RoutedEventArgs e) => Commit();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void Commit()
    {
        if (ProgramGrid.SelectedItem is not Row r) return;
        SelectedProgramName = r.Program;
        DialogResult = true;
    }

    private static string Who(ProgramVersion v)
        => string.IsNullOrEmpty(v.OperatorName) ? (v.OperatorId ?? "—") : v.OperatorName;

    private sealed record Row(string Program, string Version, string LastCommit);
}
