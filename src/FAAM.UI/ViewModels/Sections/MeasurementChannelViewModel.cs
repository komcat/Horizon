using FAAM.UI.Mvvm;
using FAAM.UI.Telemetry;

namespace FAAM.UI.ViewModels.Sections;

/// <summary>
/// One Keithley 6482 channel in the Machine-page Measurement section — a numeric
/// current readout backed by (via <c>MeasurementChartView</c>) a live trace.
/// The picoammeter polls continuously once connected, so there is no per-channel
/// Start/Stop: the readout fills as soon as the engine is polling.
/// </summary>
public sealed class MeasurementChannelViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private string _latestCurrent = "—";
    private int _sampleCount;

    public MeasurementChannelViewModel(EngineHost host, string device, int channel)
    {
        _host = host;
        Device = device;
        Channel = channel;
    }

    public string Device { get; }
    public int Channel { get; }
    public string Title => $"Channel {Channel}";

    /// <summary>Telemetry history — the chart snapshots this channel from here.</summary>
    public TelemetryStore Store => _host.Telemetry;

    /// <summary>Telemetry key of this channel's current trace.</summary>
    public string CurrentKey => $"{Device}.CH{Channel}.current";

    public string LatestCurrent { get => _latestCurrent; private set => SetField(ref _latestCurrent, value); }
    public int SampleCount { get => _sampleCount; private set => SetField(ref _sampleCount, value); }

    /// <summary>Refreshes the readout from the telemetry store — called each sampler tick.</summary>
    public void Refresh()
    {
        var channel = _host.Telemetry.GetChannel(CurrentKey);
        LatestCurrent = channel?.LatestValue is { } i ? $"{i:E4} A" : "—";
        SampleCount = channel?.Count ?? 0;
    }
}
