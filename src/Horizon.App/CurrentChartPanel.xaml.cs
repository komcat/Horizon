using System.Windows.Controls;
using Horizon.Core;
using Horizon.CurrentMeasurement;
using Horizon.LaserController;

namespace Horizon.App;

public partial class CurrentChartPanel : UserControl
{
    private string _deviceName;
    private readonly MeasurementHub _hub;
    private readonly CurrentMeasurementManager? _cmeasManager;
    private readonly LaserControllerManager? _laserManager;

    public string DeviceName => _deviceName;

    public CurrentChartPanel(string deviceName, MeasurementHub hub, List<string> channelKeys,
        CurrentMeasurementManager? cmeasManager = null, LaserControllerManager? laserManager = null)
    {
        _deviceName = deviceName;
        _hub = hub;
        _cmeasManager = cmeasManager;
        _laserManager = laserManager;

        InitializeComponent();

        // Populate device combo with both cmeas + laser devices
        var allNames = new List<string>();
        if (_cmeasManager != null)
            allNames.AddRange(_cmeasManager.GetAllDeviceNames());
        if (_laserManager != null)
            allNames.AddRange(_laserManager.GetAllDeviceNames());

        if (allNames.Count > 0)
        {
            DeviceCombo.ItemsSource = allNames;
            var idx = allNames.IndexOf(deviceName);
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
        if (DeviceCombo.SelectedItem is not string selectedName)
            return;

        // Try current measurement device first
        if (_cmeasManager != null)
        {
            var device = _cmeasManager.GetDevice(selectedName);
            if (device != null)
            {
                _deviceName = selectedName;
                var keys = new List<string>(device.ChannelCount);
                foreach (var ch in device.EnabledChannels)
                    keys.Add($"{selectedName}.CH{ch}");

                Chart.YAxisFormatter = null; // use default SI-amps formatter
                Chart.Detach();
                Chart.Attach(_hub, keys);
                Chart.StartRendering();
                return;
            }
        }

        // Try laser controller device
        if (_laserManager != null)
        {
            var device = _laserManager.GetDevice(selectedName);
            if (device != null)
            {
                _deviceName = selectedName;
                var keys = new List<string> { $"{selectedName}.Temperature", $"{selectedName}.LaserCurrent" };

                Chart.YAxisFormatter = FormatGenericValue;
                Chart.Detach();
                Chart.Attach(_hub, keys);
                Chart.StartRendering();
            }
        }
    }

    private static string FormatGenericValue(double value)
    {
        var abs = Math.Abs(value);
        if (abs < 0.001) return $"{value * 1000:F2} m";
        if (abs < 1000) return $"{value:F2}";
        return $"{value:F0}";
    }

    public void StopRendering()
    {
        Chart.StopRendering();
    }
}
