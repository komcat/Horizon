using System.Windows;
using Horizon.UI.ViewModels;

namespace Horizon.UI;

/// <summary>
/// The application shell window. Hosts the home launcher and the five pages
/// via <see cref="ShellViewModel.CurrentView"/> (see <c>docs/layer3-ui-design.md</c>
/// §6). Replaces the Phase 1 single-window <c>MainWindow</c>.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly ShellViewModel _viewModel;

    public ShellWindow(EngineHost host)
    {
        _viewModel = new ShellViewModel(host);
        DataContext = _viewModel;
        InitializeComponent();

        Closed += (_, _) => _viewModel.Dispose();
    }
}
