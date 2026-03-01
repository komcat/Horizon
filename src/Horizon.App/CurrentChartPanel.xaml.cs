using System.Windows.Controls;
using Horizon.Core;
using Horizon.CurrentMeasurement;

namespace Horizon.App;

public partial class CurrentChartPanel : UserControl
{
    private string _deviceName;
    private readonly MeasurementHub _hub;
    private readonly CurrentMeasurementManager? _cmeasManager;

    public string DeviceName => _deviceName;

    public CurrentChartPanel(string deviceName, MeasurementHub hub, List<string> channelKeys,
        CurrentMeasurementManager? cmeasManager = null)
    {
        _deviceName = deviceName;
        _hub = hub;
        _cmeasManager = cmeasManager;

        InitializeComponent();

        // Populate device combo
        if (_cmeasManager != null)
        {
            var names = _cmeasManager.GetAllDeviceNames();
            DeviceCombo.ItemsSource = names;
            // Select the current device without triggering a redundant switch
            var idx = names.IndexOf(deviceName);
            if (idx >= 0)
                DeviceCombo.SelectedIndex = idx;
        }
        else
        {
            DeviceCombo.Items.Add(deviceName);
            DeviceCombo.SelectedIndex = 0;
        }

        HeaderText.Text = "Current Chart (10s)";
        Chart.Attach(hub, channelKeys);
        Chart.StartRendering();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not string selectedName || _cmeasManager == null)
            return;

        var device = _cmeasManager.GetDevice(selectedName);
        if (device == null) return;

        _deviceName = selectedName;

        var keys = new List<string>(device.ChannelCount);
        foreach (var ch in device.EnabledChannels)
            keys.Add($"{selectedName}.CH{ch}");

        Chart.Detach();
        Chart.Attach(_hub, keys);
        Chart.StartRendering();
    }

    public void StopRendering()
    {
        Chart.StopRendering();
    }
}
