using System.Windows.Controls;

namespace FAAM.UI.Views;

/// <summary>
/// The home launcher — a tile grid. Tiles raise events the shell turns into
/// navigation; the shell owns the pages, the view just signals intent.
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>Raised when the operator clicks RUN.</summary>
    public event Action? RunRequested;

    /// <summary>Raised when the operator clicks Machine.</summary>
    public event Action? MachineRequested;

    public HomeView() => InitializeComponent();

    private void OnRunClick(object sender, System.Windows.RoutedEventArgs e) => RunRequested?.Invoke();

    private void OnMachineClick(object sender, System.Windows.RoutedEventArgs e) => MachineRequested?.Invoke();
}
