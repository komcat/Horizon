using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Horizon.IO;
using Serilog;

namespace Horizon.App;

public partial class IOOverviewPanel : UserControl
{
    private readonly EziIOManager _ioManager;
    private DispatcherTimer? _pollTimer;

    public event Action<string>? DeviceSelected;

    public IOOverviewPanel(EziIOManager ioManager)
    {
        _ioManager = ioManager;
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
        var names = _ioManager.GetAllDeviceNames();
        var rows = new List<IODeviceRow>();
        int connected = 0;

        foreach (var name in names)
        {
            var device = _ioManager.GetDevice(name);
            if (device == null) continue;

            var isConn = device.IsConnected;
            if (isConn) connected++;

            rows.Add(new IODeviceRow
            {
                Name = name,
                Status = isConn ? "Connected" : "Disconnected",
                InputCount = device.InputCount,
                OutputCount = device.OutputCount
            });
        }

        DeviceGrid.ItemsSource = rows;
        HeaderText.Text = $"IO Devices — {connected} connected, {names.Count - connected} disconnected";
    }

    private void ConnectAll_Click(object sender, RoutedEventArgs e)
    {
        _ioManager.ConnectAll();
        RefreshData();
    }

    private void DisconnectAll_Click(object sender, RoutedEventArgs e)
    {
        _ioManager.DisconnectAll();
        RefreshData();
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceGrid.SelectedItem is IODeviceRow row)
            DeviceSelected?.Invoke(row.Name);
    }

    private class IODeviceRow
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public int InputCount { get; set; }
        public int OutputCount { get; set; }
    }
}
