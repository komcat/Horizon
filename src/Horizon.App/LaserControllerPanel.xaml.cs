using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Horizon.Core;
using Horizon.LaserController;

namespace Horizon.App;

public partial class LaserControllerPanel : UserControl
{
    private readonly string _deviceName;
    private readonly LaserControllerManager _manager;
    private readonly MeasurementHub? _hub;
    private readonly string _tempChannelKey;
    private readonly string _currentChannelKey;
    private readonly DispatcherTimer _pollTimer;

    public event Action? ConnectionChanged;
    public event Action<string>? LogMessage;

    public LaserControllerPanel(string deviceName, LaserControllerManager manager, MeasurementHub? hub = null)
    {
        _deviceName = deviceName;
        _manager = manager;
        _hub = hub;
        _tempChannelKey = $"{deviceName}.Temperature";
        _currentChannelKey = $"{deviceName}.LaserCurrent";

        InitializeComponent();

        HeaderText.Text = $"Laser Controller: {deviceName}";

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += PollTimer_Tick;

        // Attach chart to hub
        if (_hub != null)
        {
            var channelKeys = new List<string> { _tempChannelKey, _currentChannelKey };
            ReadingChart.YAxisFormatter = FormatGenericValue;
            ReadingChart.Attach(_hub, channelKeys);
            ReadingChart.StartRendering();
        }

        UpdateStatus();
    }

    private static string FormatGenericValue(double value)
    {
        var abs = Math.Abs(value);
        if (abs < 0.001) return $"{value * 1000:F2} m";
        if (abs < 1000) return $"{value:F2}";
        return $"{value:F0}";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var result = _manager.ConnectDevice(_deviceName);
        Log(result ? $"Connected '{_deviceName}'" : $"Connect '{_deviceName}' FAILED");
        ConnectionChanged?.Invoke();
        UpdateStatus();
        if (result)
            _pollTimer.Start();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        var result = _manager.DisconnectDevice(_deviceName);
        Log(result ? $"Disconnected '{_deviceName}'" : $"Disconnect '{_deviceName}' FAILED");
        ConnectionChanged?.Invoke();
        UpdateStatus();
    }

    private void SetTemp_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(TempSetpointInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
        {
            Log("Invalid temperature value");
            return;
        }
        var result = _manager.SetTemperature(_deviceName, temp);
        Log($"SetTemperature({temp:F1} C): {result}");
    }

    private void TECOn_Click(object sender, RoutedEventArgs e)
    {
        var result = _manager.TECOn(_deviceName);
        Log($"TEC ON: {result}");
    }

    private void TECOff_Click(object sender, RoutedEventArgs e)
    {
        var result = _manager.TECOff(_deviceName);
        Log($"TEC OFF: {result}");
    }

    private void SetLaserCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(LaserCurrentInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var current))
        {
            Log("Invalid current value");
            return;
        }
        var result = _manager.SetLaserCurrent(_deviceName, current);
        Log($"SetLaserCurrent({current:F1} mA): {result}");
    }

    private void LaserOn_Click(object sender, RoutedEventArgs e)
    {
        var result = _manager.LaserOn(_deviceName);
        Log($"Laser ON: {result}");
    }

    private void LaserOff_Click(object sender, RoutedEventArgs e)
    {
        var result = _manager.LaserOff(_deviceName);
        Log($"Laser OFF: {result}");
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var device = _manager.GetDevice(_deviceName);
        if (device == null)
        {
            StatusText.Text = "Device not found";
            return;
        }

        var connected = device.IsConnected;
        StatusText.Text = connected ? "Connected" : "Disconnected";

        if (!connected)
        {
            LiveTempText.Text = "--";
            LiveCurrentText.Text = "--";
            DeviceStatusText.Text = "Not connected";
            return;
        }

        var status = device.GetStatus();

        // Push to MeasurementHub for chart consumers
        if (_hub != null)
        {
            var now = DateTimeOffset.UtcNow;
            if (!IsThermistorDisconnected(status.CurrentTemperature))
                _hub.Push(_tempChannelKey, status.CurrentTemperature, now);
            _hub.Push(_currentChannelKey, status.CurrentLaserCurrent, now);
        }

        var tempText = IsThermistorDisconnected(status.CurrentTemperature)
            ? "No Thermistor"
            : $"{status.CurrentTemperature:F2} C";

        LiveTempText.Text = tempText;
        LiveCurrentText.Text = $"{status.CurrentLaserCurrent:F2} mA";

        var actualTempText = IsThermistorDisconnected(status.CurrentTemperature)
            ? "No Thermistor"
            : $"{status.CurrentTemperature:F2} C";

        DeviceStatusText.Text =
            $"TEC: {(status.TECOn ? "ON" : "OFF")}  |  Setpoint: {status.SetpointTemperature:F1} C  |  Actual: {actualTempText}\n" +
            $"Laser: {(status.LaserOn ? "ON" : "OFF")}  |  Setpoint: {status.SetpointLaserCurrent:F1} mA  |  Actual: {status.CurrentLaserCurrent:F2} mA" +
            (string.IsNullOrEmpty(status.LastError) ? "" : $"\nError: {status.LastError}");
    }

    private static bool IsThermistorDisconnected(double temperature)
    {
        return temperature < -1000 || temperature > 1000 || double.IsNaN(temperature) || double.IsInfinity(temperature);
    }

    public void StopPolling()
    {
        _pollTimer.Stop();
        ReadingChart.StopRendering();
    }

    private void Log(string message)
    {
        LogMessage?.Invoke($"[LaserCtrl] {message}");
    }
}
