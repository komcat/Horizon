using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Horizon.Pneumatic;
using Serilog;

namespace Horizon.App;

public partial class PneumaticOverviewPanel : UserControl
{
    private readonly PneumaticManager _pneumaticManager;
    private DispatcherTimer? _pollTimer;

    public event Action<string>? DeviceSelected;

    public PneumaticOverviewPanel(PneumaticManager pneumaticManager)
    {
        _pneumaticManager = pneumaticManager;
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
        var names = _pneumaticManager.GetAllSlideNames();
        var rows = new List<PneumaticDeviceRow>();

        foreach (var name in names)
        {
            var slide = _pneumaticManager.GetSlide(name);
            if (slide == null) continue;

            var status = slide.GetStatus();
            rows.Add(new PneumaticDeviceRow
            {
                Name = name,
                Position = status.Position.ToString(),
                ExtSensor = status.ExtendedSensorActive ? "ON" : "OFF",
                RetSensor = status.RetractedSensorActive ? "ON" : "OFF"
            });
        }

        DeviceGrid.ItemsSource = rows;
        HeaderText.Text = $"Pneumatic Slides — {names.Count} device(s)";
    }

    private void RetractAll_Click(object sender, RoutedEventArgs e)
    {
        _pneumaticManager.StopAll();
        RefreshData();
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceGrid.SelectedItem is PneumaticDeviceRow row)
            DeviceSelected?.Invoke(row.Name);
    }

    private class PneumaticDeviceRow
    {
        public string Name { get; set; } = "";
        public string Position { get; set; } = "";
        public string ExtSensor { get; set; } = "";
        public string RetSensor { get; set; } = "";
    }
}
