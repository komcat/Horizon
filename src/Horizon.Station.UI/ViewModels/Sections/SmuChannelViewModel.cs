
namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// One SMU channel in the Machine-page SMU section — its own streaming
/// Start/Stop, numeric readout, and (via <c>SmuChartView</c>) a live current
/// chart. Channels of one device stream independently.
/// </summary>
public sealed class SmuChannelViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly Func<int> _intervalMs;
    private bool _isStreaming;
    private string _latestCurrent = "—";
    private string _latestVoltage = "—";
    private int _sampleCount;

    public SmuChannelViewModel(EngineHost host, string device, int channel, Func<int> intervalMs)
    {
        _host = host;
        Device = device;
        Channel = channel;
        _intervalMs = intervalMs;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !_isStreaming);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _isStreaming);
    }

    public string Device { get; }
    public int Channel { get; }
    public string Title => $"Channel {Channel}";

    /// <summary>Telemetry history — the chart snapshots this channel from here.</summary>
    public TelemetryStore Store => _host.Telemetry;

    /// <summary>Telemetry key of this channel's current trace.</summary>
    public string CurrentKey => $"{Device}.CH{Channel}.current";

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

    public string LatestCurrent { get => _latestCurrent; private set => SetField(ref _latestCurrent, value); }
    public string LatestVoltage { get => _latestVoltage; private set => SetField(ref _latestVoltage, value); }
    public int SampleCount { get => _sampleCount; private set => SetField(ref _sampleCount, value); }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }

    /// <summary>Refreshes the readout from the telemetry store — called each sampler tick.</summary>
    public void Refresh()
    {
        IsStreaming = _host.Engine.Smu.IsStreaming(Device, Channel);
        var iChannel = _host.Telemetry.GetChannel(CurrentKey);
        var vChannel = _host.Telemetry.GetChannel($"{Device}.CH{Channel}.voltage");
        LatestCurrent = iChannel?.LatestValue is { } i ? $"{i:E4} A" : "—";
        LatestVoltage = vChannel?.LatestValue is { } v ? $"{v:F4} V" : "—";
        SampleCount = iChannel?.Count ?? 0;
    }

    private async Task StartAsync()
    {
        await _host.Engine.Smu.StartStreamingAsync(Device, Channel, _intervalMs()).ConfigureAwait(true);
        IsStreaming = _host.Engine.Smu.IsStreaming(Device, Channel);
    }

    private async Task StopAsync()
    {
        await _host.Engine.Smu.StopStreamingAsync(Device, Channel).ConfigureAwait(true);
        IsStreaming = _host.Engine.Smu.IsStreaming(Device, Channel);
    }
}
