using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Horizon.Camera;
using Serilog;

namespace Horizon.App;

public partial class CameraOverviewPanel : UserControl
{
    private readonly CameraManager _cameraManager;
    private DispatcherTimer? _pollTimer;

    public event Action<string>? DeviceSelected;

    public CameraOverviewPanel(CameraManager cameraManager)
    {
        _cameraManager = cameraManager;
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
        var statuses = _cameraManager.GetAllCameraStatus();
        var rows = new List<CameraDeviceRow>();
        int connected = 0;

        foreach (var s in statuses)
        {
            if (s.Connected) connected++;

            rows.Add(new CameraDeviceRow
            {
                Id = s.Id,
                ModelName = string.IsNullOrEmpty(s.ModelName) ? s.Id : s.ModelName,
                SerialNumber = s.SerialNumber,
                TypeName = s.TypeName,
                Connected = s.Connected ? "Yes" : "No",
                Grabbing = s.Grabbing ? "Yes" : "No"
            });
        }

        DeviceGrid.ItemsSource = rows;
        HeaderText.Text = $"Cameras — {connected} connected, {statuses.Count - connected} disconnected";
    }

    private void ConnectAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = _cameraManager.GetCameraIds();
        Log.Information("Camera: Connect All ({Count} cameras)", ids.Count);
        foreach (var id in ids)
        {
            var ok = _cameraManager.ConnectCamera(id);
            Log.Information("Camera '{CameraId}': Connect {Result}", id, ok ? "OK" : "FAILED");
        }
        RefreshData();
    }

    private void DisconnectAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = _cameraManager.GetCameraIds();
        Log.Information("Camera: Disconnect All ({Count} cameras)", ids.Count);
        foreach (var id in ids)
        {
            var ok = _cameraManager.DisconnectCamera(id);
            Log.Information("Camera '{CameraId}': Disconnect {Result}", id, ok ? "OK" : "FAILED");
        }
        RefreshData();
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceGrid.SelectedItem is CameraDeviceRow row)
            DeviceSelected?.Invoke(row.Id);
    }

    private class CameraDeviceRow
    {
        public string Id { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Connected { get; set; } = "";
        public string Grabbing { get; set; } = "";
    }
}
