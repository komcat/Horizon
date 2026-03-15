using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Horizon.Motion;
using Serilog;

namespace Horizon.App;

public partial class MotionOverviewPanel : UserControl
{
    private readonly UnifiedMotionManager _motionManager;
    private DispatcherTimer? _pollTimer;

    public event Action<string>? DeviceSelected;

    public MotionOverviewPanel(UnifiedMotionManager motionManager)
    {
        _motionManager = motionManager;
        InitializeComponent();
        RefreshData();
        StartPolling();
    }

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += (_, _) => RefreshData();
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void RefreshData()
    {
        var names = _motionManager.GetAllDeviceNames();
        var rows = new List<MotionDeviceRow>();
        int connected = 0;

        foreach (var name in names)
        {
            var ctrl = _motionManager.GetController(name);
            if (ctrl == null) continue;

            var isConn = ctrl.IsConnected;
            if (isConn) connected++;

            var posStr = "";
            if (isConn)
            {
                var pos = ctrl.GetCurrentPosition();
                posStr = $"X:{pos.X:F3}  Y:{pos.Y:F3}  Z:{pos.Z:F3}";
            }

            rows.Add(new MotionDeviceRow
            {
                Name = name,
                ControllerType = ctrl.ControllerType,
                Status = isConn ? "Connected" : "Disconnected",
                AxisCount = ctrl.AxisNames.Count,
                Position = posStr
            });
        }

        DeviceGrid.ItemsSource = rows;
        HeaderText.Text = $"Motion Controllers — {connected} connected, {names.Count - connected} disconnected";
    }

    private void ConnectAll_Click(object sender, RoutedEventArgs e)
    {
        _motionManager.ConnectAll();
        RefreshData();
    }

    private void DisconnectAll_Click(object sender, RoutedEventArgs e)
    {
        _motionManager.DisconnectAll();
        RefreshData();
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _motionManager.StopAllDevices();
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceGrid.SelectedItem is MotionDeviceRow row)
            DeviceSelected?.Invoke(row.Name);
    }

    private class MotionDeviceRow
    {
        public string Name { get; set; } = "";
        public string ControllerType { get; set; } = "";
        public string Status { get; set; } = "";
        public int AxisCount { get; set; }
        public string Position { get; set; } = "";
    }
}
