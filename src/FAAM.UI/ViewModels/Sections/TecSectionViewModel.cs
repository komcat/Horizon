using Horizon.Common;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's TEC section — a standalone TEC controller picker (e.g.
/// Thorlabs TED4015, TTC-001) with a live temperature readout, setpoint editor
/// and TEC ON/OFF. The CLD101x (laser + TEC) stays on the Laser section.
/// Refreshed from the engine's cached <c>TecState</c> by the shared sampler.
/// </summary>
public sealed class TecSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private bool _connected;
    private bool _tecOn;
    private string _temperature = "—";
    private string _setpointTemperature = "—";
    private double _temperatureSetpoint = 25.0;
    private string _status = "—";
    private bool _refreshInFlight;

    public TecSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Tec.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;

        SetTemperatureCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.Tec.SetTemperatureAsync(d, _temperatureSetpoint),
                           $"TEC setpoint = {_temperatureSetpoint:G} °C"), HasDevice);
        TecOnCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Tec.TecOnAsync, "TEC on"), HasDevice);
        TecOffCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Tec.TecOffAsync, "TEC off"), HasDevice);
        ConnectCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.Tec.ConnectAsync(d), "Connect"), HasDevice);
        DisconnectCommand = new AsyncRelayCommand(
            () => RunAsync(_host.Engine.Tec.DisconnectAsync, "Disconnect"), HasDevice);
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "TEC";

    public IReadOnlyList<string> Devices { get; }

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set => SetField(ref _selectedDevice, value);
    }

    public bool Connected { get => _connected; private set => SetField(ref _connected, value); }
    public bool TecOn { get => _tecOn; private set => SetField(ref _tecOn, value); }

    public string Temperature { get => _temperature; private set => SetField(ref _temperature, value); }
    public string SetpointTemperature { get => _setpointTemperature; private set => SetField(ref _setpointTemperature, value); }

    /// <summary>TEC temperature setpoint (°C) applied by <see cref="SetTemperatureCommand"/>.</summary>
    public double TemperatureSetpoint
    {
        get => _temperatureSetpoint;
        set => SetField(ref _temperatureSetpoint, value);
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public AsyncRelayCommand SetTemperatureCommand { get; }
    public AsyncRelayCommand TecOnCommand { get; }
    public AsyncRelayCommand TecOffCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>
    /// Drives one hardware poll (forces the engine to re-read the TEC via
    /// <see cref="ITecFacet.RefreshAsync"/>) and updates the WPF bindings from
    /// the resulting cached state. Called each sampler tick (5 Hz); guarded
    /// against re-entry so a slow USB round-trip cannot queue duplicate refreshes.
    /// The engine's own 2 s background poll still runs as a safety net.
    /// </summary>
    public async Task RefreshAsync()
    {
        var device = _selectedDevice;
        if (device == null)
        {
            Connected = false;
            return;
        }
        if (_refreshInFlight) return;

        _refreshInFlight = true;
        try
        {
            // RefreshAsync touches the adapter lock (briefly held by the 2 s
            // facet poll during a SCPI/USB query) — run off the UI thread.
            await Task.Run(() => _host.Engine.Tec.RefreshAsync()).ConfigureAwait(true);

            Connected = _host.Engine.Tec.IsConnected(device);
            var state = _host.Engine.Tec.GetState(device);
            TecOn = state.TecOn;
            Temperature = FormatCelsius(state.Temperature);
            SetpointTemperature = FormatCelsius(state.SetpointTemperature);
            if (!string.IsNullOrEmpty(state.LastError))
                Status = state.LastError;
        }
        finally { _refreshInFlight = false; }
    }

    private bool HasDevice() => _selectedDevice != null;

    private async Task RunAsync(Func<string, Task<Result>> operation, string okMessage)
    {
        var device = _selectedDevice;
        if (device == null) return;
        var result = await operation(device).ConfigureAwait(true);
        Status = result.IsSuccess ? okMessage : result.Message;
    }

    // TTC-001 returns NaN when the thermistor is disconnected; guard the
    // display so it never shows garbage.
    private static string FormatCelsius(double celsius)
        => double.IsFinite(celsius) && Math.Abs(celsius) < 1e6 ? $"{celsius:F2} °C" : "—";
}
