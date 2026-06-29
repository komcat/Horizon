using System.Windows.Controls;
using FAAM.UI.ViewModels;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page's "Find First Light" tab — a settings form over the post-FAU grid
/// search tunables (<see cref="FindFirstLightSettingsViewModel"/>). A static form: no
/// background subscriptions, so it needs no Activate/Deactivate.
/// </summary>
public partial class FindFirstLightSettingsView : UserControl
{
    public FindFirstLightSettingsView(EngineHost host)
    {
        InitializeComponent();
        DataContext = new FindFirstLightSettingsViewModel(host);
    }
}
