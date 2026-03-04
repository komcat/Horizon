using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Horizon.Pneumatic;
using Horizon.Pneumatic.Interfaces;
using Horizon.Pneumatic.Models;
using Serilog;

namespace Horizon.App;

public partial class PneumaticPanel : UserControl
{
    private readonly string _slideName;
    private readonly PneumaticManager _pneumaticManager;
    private readonly IPneumaticSlide _slide;
    private DispatcherTimer? _pollTimer;
    private PneumaticPosition _lastPosition = PneumaticPosition.Unknown;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x4E, 0xC9, 0x4E));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly SolidColorBrush BlueBrush = new(Color.FromRgb(0x64, 0xB5, 0xF6));

    public event Action? StatusChanged;

    public PneumaticPanel(string slideName, PneumaticManager pneumaticManager)
    {
        _slideName = slideName;
        _pneumaticManager = pneumaticManager;
        _slide = pneumaticManager.GetSlide(slideName)!;

        InitializeComponent();

        HeaderText.Text = slideName;
        UpdateDisplay();
        StartPolling();
    }

    private void Extend_Click(object sender, RoutedEventArgs e)
    {
        var err = _slide.Extend();
        Log.Information("Pneumatic '{SlideName}': Extend {Result}", _slideName, err == PneumaticError.Success ? "OK" : err.ToString());
        UpdateDisplay();
        StatusChanged?.Invoke();
    }

    private void Retract_Click(object sender, RoutedEventArgs e)
    {
        var err = _slide.Retract();
        Log.Information("Pneumatic '{SlideName}': Retract {Result}", _slideName, err == PneumaticError.Success ? "OK" : err.ToString());
        UpdateDisplay();
        StatusChanged?.Invoke();
    }

    private void RetractAll_Click(object sender, RoutedEventArgs e)
    {
        _pneumaticManager.StopAll();
        Log.Information("Pneumatic: Retract All");
        UpdateDisplay();
        StatusChanged?.Invoke();
    }

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _pollTimer.Tick += (_, _) => UpdateDisplay();
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void UpdateDisplay()
    {
        var status = _slide.GetStatus();

        StatusText.Text = status.Valid ? $"IO linked — {_slide.SlideName}" : "IO not available";

        PositionText.Text = status.Position.ToString();
        PositionText.Foreground = status.Position switch
        {
            PneumaticPosition.Extended => GreenBrush,
            PneumaticPosition.Retracted => BlueBrush,
            PneumaticPosition.Extending or PneumaticPosition.Retracting => AmberBrush,
            PneumaticPosition.HardwareError => RedBrush,
            _ => GrayBrush
        };

        var ext = status.ExtendedSensorActive ? "ON" : "OFF";
        var ret = status.RetractedSensorActive ? "ON" : "OFF";
        var outp = status.OutputActive ? "ON" : "OFF";
        SensorText.Text = $"ExtSensor: {ext}  |  RetSensor: {ret}  |  Output: {outp}";

        LastMoveText.Text = string.Empty;

        // Notify tree when position changes during polling
        if (status.Position != _lastPosition)
        {
            _lastPosition = status.Position;
            StatusChanged?.Invoke();
        }
    }
}
