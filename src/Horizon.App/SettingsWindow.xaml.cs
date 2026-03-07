using System.Windows;
using Horizon.Common;

namespace Horizon.App;

public partial class SettingsWindow : Window
{
    private readonly UserPreferences _prefs;

    public SettingsWindow(UserPreferences prefs)
    {
        _prefs = prefs;
        InitializeComponent();

        AutoOpenTopViewCheck.IsChecked = prefs.AutoOpenTopView;
        AutoStartMeasPollingCheck.IsChecked = prefs.AutoStartMeasurementPolling;
        ShowCrosshairCheck.IsChecked = prefs.ShowCrosshair;
        ClickToMoveCheck.IsChecked = prefs.ClickToMoveEnabled;
        ShowChartCheck.IsChecked = prefs.ShowChartOverlay;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _prefs.AutoOpenTopView = AutoOpenTopViewCheck.IsChecked == true;
        _prefs.AutoStartMeasurementPolling = AutoStartMeasPollingCheck.IsChecked == true;
        _prefs.ShowCrosshair = ShowCrosshairCheck.IsChecked == true;
        _prefs.ClickToMoveEnabled = ClickToMoveCheck.IsChecked == true;
        _prefs.ShowChartOverlay = ShowChartCheck.IsChecked == true;
        _prefs.Save();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
