using Horizon.UI.Mvvm;
using Horizon.UI.Telemetry;

namespace Horizon.UI.ViewModels.Sections;

/// <summary>
/// The Machine page's SMU section — streaming control + a live numeric readout.
/// Start/Stop drive the Layer 1 streaming loop (<c>ISmuFacet.StartStreamingAsync</c>);
/// the readout is refreshed each Machine-page sampler tick from the
/// <see cref="Telemetry.TelemetryStore"/> the engine host fills.
/// The trend chart arrives in Phase 5.
/// </summary>
public sealed class SmuSectionViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string? _selectedDevice;
    private int _channel = 1;
    private int _intervalMs = 100;
    private bool _isStreaming;
    private bool _connected;
    private string _latestCurrent = "—";
    private string _latestVoltage = "—";
    private int _sampleCount;

    public SmuSectionViewModel(EngineHost host)
    {
        _host = host;
        Devices = host.Engine.Smu.DeviceNames;
        _selectedDevice = Devices.Count > 0 ? Devices[0] : null;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_isStreaming && _selectedDevice != null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _isStreaming);
    }

    /// <summary>Label shown in the Machine-page section rail.</summary>
    public string Title => "SMU";

    /// <summary>Telemetry history — the trend chart snapshots channels from here.</summary>
    public TelemetryStore Store => _host.Telemetry;

    public IReadOnlyList<string> Devices { get; }

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetField(ref _selectedDevice, value)) StartCommand.RaiseCanExecuteChanged(); }
    }

    public int Channel
    {
        get => _channel;
        set => SetField(ref _channel, value);
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set => SetField(ref _intervalMs, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (!SetField(ref _isStreaming, value)) return;
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public bool Connected
    {
        get => _connected;
        private set => SetField(ref _connected, value);
    }

    public string LatestCurrent
    {
        get => _latestCurrent;
        private set => SetField(ref _latestCurrent, value);
    }

    public string LatestVoltage
    {
        get => _latestVoltage;
        private set => SetField(ref _latestVoltage, value);
    }

    public int SampleCount
    {
        get => _sampleCount;
        private set => SetField(ref _sampleCount, value);
    }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }

    /// <summary>Refreshes the readout from the telemetry store — called each sampler tick.</summary>
    public void Refresh()
    {
        var device = _selectedDevice;
        if (device == null) return;

        Connected = _host.Engine.Smu.IsConnected(device);
        IsStreaming = _host.Engine.Smu.IsStreaming(device);

        var iChannel = _host.Telemetry.GetChannel($"{device}.CH{_channel}.current");
        var vChannel = _host.Telemetry.GetChannel($"{device}.CH{_channel}.voltage");
        LatestCurrent = iChannel?.LatestValue is { } i ? $"{i:E4} A" : "—";
        LatestVoltage = vChannel?.LatestValue is { } v ? $"{v:F4} V" : "—";
        SampleCount = iChannel?.Count ?? 0;
    }

    private async Task StartAsync()
    {
        if (_selectedDevice == null) return;
        await _host.Engine.Smu.StartStreamingAsync(_selectedDevice, _channel, _intervalMs)
                  .ConfigureAwait(true);
        IsStreaming = _host.Engine.Smu.IsStreaming(_selectedDevice);
    }

    private async Task StopAsync()
    {
        if (_selectedDevice == null) return;
        await _host.Engine.Smu.StopStreamingAsync(_selectedDevice).ConfigureAwait(true);
        IsStreaming = _host.Engine.Smu.IsStreaming(_selectedDevice);
    }

    /// <summary>Stops streaming if active — called when the Machine page is disposed.</summary>
    public async Task StopIfStreamingAsync()
    {
        if (_selectedDevice != null && _host.Engine.Smu.IsStreaming(_selectedDevice))
            await _host.Engine.Smu.StopStreamingAsync(_selectedDevice).ConfigureAwait(false);
    }
}
