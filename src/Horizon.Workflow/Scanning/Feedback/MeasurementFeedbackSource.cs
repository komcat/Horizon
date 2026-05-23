using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Scanning.Feedback;

/// <summary>
/// Scan feedback from a current-measurement device (Keithley 6482). Puts the
/// device into fast streaming and caches one channel from each
/// <see cref="IMeasurementFacet.StateChanged"/> update.
/// </summary>
internal sealed class MeasurementFeedbackSource : FeedbackSourceBase
{
    private readonly IHorizonEngine _engine;
    private readonly string _device;
    private readonly int _channelIndex;   // 0-based
    private bool _subscribed;

    public MeasurementFeedbackSource(IHorizonEngine engine, FeedbackDescriptor descriptor)
    {
        _engine = engine;
        _device = descriptor.Device;
        _channelIndex = Math.Max(0, descriptor.Channel - 1);
    }

    public override string Description => $"Measurement {_device} CH{_channelIndex + 1}";

    public override string Unit
    {
        get
        {
            var unit = _engine.Measurement.GetState(_device).Unit;
            return string.IsNullOrEmpty(unit) ? "A" : unit;
        }
    }

    public override async Task<Result> StartAsync(CancellationToken ct)
    {
        if (_engine.Measurement.GetDevice(_device) == null)
            return Result.Fail(ErrorCode.CMeasDeviceNotFound, $"Measurement device '{_device}' not found.");
        if (!_engine.Measurement.IsConnected(_device))
            return Result.Fail(ErrorCode.CMeasDisconnected, $"Measurement device '{_device}' is not connected.");

        _engine.Measurement.StateChanged += OnChanged;
        _subscribed = true;

        // Seed from the cached state so HasValidSample can become true promptly.
        var state = _engine.Measurement.GetState(_device);
        if (state.Valid && _channelIndex < state.Channels.Count)
            Publish(state.Channels[_channelIndex]);

        var result = await _engine.Measurement.StartStreamingAsync(_device).ConfigureAwait(false);
        if (!result.IsSuccess)
            await StopAsync().ConfigureAwait(false);
        return result;
    }

    private void OnChanged(MeasurementChanged change)
    {
        if (!string.Equals(change.Device, _device, StringComparison.OrdinalIgnoreCase))
            return;
        var channels = change.State.Channels;
        if (change.State.Valid && _channelIndex < channels.Count)
            Publish(channels[_channelIndex]);
    }

    public override async Task StopAsync()
    {
        if (_subscribed)
        {
            _engine.Measurement.StateChanged -= OnChanged;
            _subscribed = false;
        }
        await _engine.Measurement.StopStreamingAsync(_device).ConfigureAwait(false);
    }
}
