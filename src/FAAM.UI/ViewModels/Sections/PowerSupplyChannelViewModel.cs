using Horizon.Common;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One channel of a power supply on the Machine page — V/I setpoint editors,
/// output toggle, and live readings refreshed from the cached
/// <see cref="Horizon.Engine.PowerSupplyChannelState"/>.
/// </summary>
public sealed class PowerSupplyChannelViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly Func<string?> _device;
    private double _voltageSetpoint;
    private double _currentSetpoint;
    private string _voltage = "—";
    private string _current = "—";
    private bool _outputOn;
    private string? _lastStatus;

    public PowerSupplyChannelViewModel(EngineHost host, Func<string?> device, int channel)
    {
        _host = host;
        _device = device;
        Channel = channel;

        SetVoltageCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.PowerSupply.SetVoltageAsync(d, channel, _voltageSetpoint),
                           $"CH{channel} V = {_voltageSetpoint:G}"));
        SetCurrentLimitCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.PowerSupply.SetCurrentLimitAsync(d, channel, _currentSetpoint),
                           $"CH{channel} I-lim = {_currentSetpoint:G}"));
        OutputOnCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.PowerSupply.OutputOnAsync(d, channel),
                           $"CH{channel} ON"));
        OutputOffCommand = new AsyncRelayCommand(
            () => RunAsync(d => _host.Engine.PowerSupply.OutputOffAsync(d, channel),
                           $"CH{channel} OFF"));
    }

    public int Channel { get; }
    public string Title => $"CH{Channel}";

    public double VoltageSetpoint { get => _voltageSetpoint; set => SetField(ref _voltageSetpoint, value); }
    public double CurrentSetpoint { get => _currentSetpoint; set => SetField(ref _currentSetpoint, value); }

    /// <summary>Live measured voltage, or "—" when no reading is available.</summary>
    public string Voltage { get => _voltage; private set => SetField(ref _voltage, value); }

    /// <summary>Live measured current, or "—" when no reading is available.</summary>
    public string Current { get => _current; private set => SetField(ref _current, value); }

    public bool OutputOn { get => _outputOn; private set => SetField(ref _outputOn, value); }

    /// <summary>
    /// Status of the last command issued from this channel — surfaced on the
    /// parent section's status line.
    /// </summary>
    public string? LastStatus { get => _lastStatus; private set => SetField(ref _lastStatus, value); }

    public AsyncRelayCommand SetVoltageCommand { get; }
    public AsyncRelayCommand SetCurrentLimitCommand { get; }
    public AsyncRelayCommand OutputOnCommand { get; }
    public AsyncRelayCommand OutputOffCommand { get; }

    /// <summary>Updates the live readings from a per-channel state snapshot.</summary>
    public void Apply(Horizon.Engine.PowerSupplyChannelState? state)
    {
        if (state == null)
        {
            Voltage = "—";
            Current = "—";
            OutputOn = false;
            return;
        }
        Voltage = $"{state.Voltage:F3} V";
        Current = $"{state.Current:F4} A";
        OutputOn = state.OutputOn;
    }

    private async Task RunAsync(Func<string, Task<Result>> operation, string okMessage)
    {
        var device = _device();
        if (device == null) return;
        var result = await operation(device).ConfigureAwait(true);
        LastStatus = result.IsSuccess ? okMessage : result.Message;
    }
}
