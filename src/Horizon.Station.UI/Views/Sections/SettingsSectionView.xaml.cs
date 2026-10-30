using System.Windows;
using System.Windows.Controls;
using Horizon.Station.UI.ViewModels.Sections;

namespace Horizon.Station.UI.Views.Sections;

/// <summary>
/// Machine-page Settings section — application settings, the station's machine
/// identity, and the user directory. Bound to <c>SettingsSectionViewModel</c>.
/// Admin-only: the Machine page leaves this section out of its rail below that level.
/// </summary>
public partial class SettingsSectionView : UserControl
{
    public SettingsSectionView() => InitializeComponent();

    /// <summary>
    /// Applies the typed password to the selected level. WPF deliberately refuses to
    /// bind <see cref="PasswordBox.Password"/>, so the view reads it and hands it to the
    /// view-model rather than the view-model reaching for a control. Clearing the box
    /// and pressing Set removes the level's password.
    /// </summary>
    private void OnSetLevelPassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsSectionViewModel vm) return;

        vm.ApplyLevelPassword(LevelPasswordBox.Password);
        LevelPasswordBox.Clear();
    }
}
