using Horizon.Common;

namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's Laser section — a Thorlabs CLD101x picker with a TEC
/// (temperature) group and a laser-diode (current) group, each showing a live
/// readout, the setpoint, a setter, and on/off. Refreshed from the engine's
/// cached <c>LaserState</c> by the shared sampler each tick.
/// </summary>
public sealed class LaserSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private bool _tecOn;
    private bool _laserOn;
    private string _temperature = "—";
    private string _setpointTemperature = "—";
    private string _laserCurrent = "—";
    private string _setpointLaserCurrent = "—";
    private double _temperatureSetpoint = 25.0;
    private double _laserCurrentSetpoint;
    private string _status = "—";

    public LaserSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Laser.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;

        SetTemperatureCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.Laser.SetTemperatureAsync(d, _temperatureSetpoint),
                           $"TEC setpoint = {_temperatureSetpoint:G} °C"), HasDevice);
        TecOnCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Laser.TecOnAsync, "TEC on"), HasDevice);
        TecOffCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Laser.TecOffAsync, "TEC off"), HasDevice);
        SetLaserCurrentCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.Laser.SetLaserCurrentAsync(d, _laserCurrentSetpoint),
                           $"Laser setpoint = {_laserCurrentSetpoint:G} mA"), HasDevice);
        LaserOnCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Laser.LaserOnAsync, "Laser ON"), HasDevice);
        LaserOffCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Laser.LaserOffAsync, "Laser off"), HasDevice);
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "Laser";

    public IReadOnlyList<string> Devices { get; }

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set => SetField(ref _selectedDevice, value);
    }

    public bool Connected { get => _connected; private set => SetField(ref _connected, value); }
    public bool TecOn { get => _tecOn; private set => SetField(ref _tecOn, value); }
    public bool LaserOn { get => _laserOn; private set => SetField(ref _laserOn, value); }

    /// <summary>Live TEC temperature, or "—" when the reading is not available.</summary>
    public string Temperature { get => _temperature; private set => SetField(ref _temperature, value); }
    public string SetpointTemperature { get => _setpointTemperature; private set => SetField(ref _setpointTemperature, value); }

    /// <summary>Live laser-diode current, or "—" when the reading is not available.</summary>
    public string LaserCurrent { get => _laserCurrent; private set => SetField(ref _laserCurrent, value); }
    public string SetpointLaserCurrent { get => _setpointLaserCurrent; private set => SetField(ref _setpointLaserCurrent, value); }

    /// <summary>TEC temperature setpoint (°C) applied by <see cref="SetTemperatureCommand"/>.</summary>
    public double TemperatureSetpoint
    {
        get => _temperatureSetpoint;
        set => SetField(ref _temperatureSetpoint, value);
    }

    /// <summary>Laser-diode current setpoint (mA) applied by <see cref="SetLaserCurrentCommand"/>.</summary>
    public double LaserCurrentSetpoint
    {
        get => _laserCurrentSetpoint;
        set => SetField(ref _laserCurrentSetpoint, value);
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public AsyncRelayCommand SetTemperatureCommand { get; }
    public AsyncRelayCommand TecOnCommand { get; }
    public AsyncRelayCommand TecOffCommand { get; }
    public AsyncRelayCommand SetLaserCurrentCommand { get; }
    public AsyncRelayCommand LaserOnCommand { get; }
    public AsyncRelayCommand LaserOffCommand { get; }

    /// <summary>Refreshes connection state and every readout — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        Connected = device != null && _host.Engine.Laser.IsConnected(device);
        if (device == null) return;

        var state = _host.Engine.Laser.GetState(device);
        TecOn = state.TecOn;
        LaserOn = state.LaserOn;
        Temperature = FormatCelsius(state.Temperature);
        SetpointTemperature = FormatCelsius(state.SetpointTemperature);
        LaserCurrent = FormatMilliamps(state.LaserCurrent);
        SetpointLaserCurrent = FormatMilliamps(state.SetpointLaserCurrent);
    }

    private bool HasDevice() => _selectedDevice != null;

    private async Task RunAsync(Func<string, Task<Result>> operation, string okMessage)
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await operation(device).ConfigureAwait(true);
        Status = result.IsSuccess ? okMessage : result.Message;
    }

    // The CLD101x can return a SCPI overrange/NaN sentinel (~9.9e37) for an
    // unavailable measurement — guard the display so it never shows garbage.
    private static string FormatCelsius(double celsius)
        => double.IsFinite(celsius) && Math.Abs(celsius) < 1e6 ? $"{celsius:F2} °C" : "—";

    private static string FormatMilliamps(double milliamps)
        => double.IsFinite(milliamps) && Math.Abs(milliamps) < 1e9 ? $"{milliamps:F2} mA" : "—";
}
