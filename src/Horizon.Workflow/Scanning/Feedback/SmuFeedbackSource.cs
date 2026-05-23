using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Scanning.Feedback;

/// <summary>
/// Scan feedback from a source-measure unit. Streams one channel via
/// <see cref="ISmuFacet.StartStreamingAsync"/> and caches each
/// <see cref="ISmuFacet.Sampled"/> sample.
/// </summary>
internal sealed class SmuFeedbackSource : FeedbackSourceBase
{
    private readonly IHorizonEngine _engine;
    private readonly string _device;
    private readonly int _channel;
    private readonly FeedbackValue _valueType;
    private readonly int _intervalMs;
    private bool _subscribed;

    public SmuFeedbackSource(IHorizonEngine engine, FeedbackDescriptor descriptor)
    {
        _engine = engine;
        _device = descriptor.Device;
        _channel = descriptor.Channel;
        _valueType = descriptor.ValueType;
        _intervalMs = Math.Clamp(descriptor.IntervalMs, 10, 1000);
    }

    public override string Description =>
        $"SMU {_device} CH{_channel} {_valueType.ToString().ToLowerInvariant()}";

    public override string Unit => _valueType == FeedbackValue.Voltage ? "V" : "A";

    protected override int SampleSpacingMs => Math.Max(50, _intervalMs);

    public override async Task<Result> StartAsync(CancellationToken ct)
    {
        if (_engine.Smu.GetDevice(_device) == null)
            return Result.Fail(ErrorCode.OperationFailed, $"SMU '{_device}' not found.");
        if (!_engine.Smu.IsConnected(_device))
            return Result.Fail(ErrorCode.OperationFailed, $"SMU '{_device}' is not connected.");

        _engine.Smu.Sampled += OnSampled;
        _subscribed = true;

        var result = await _engine.Smu.StartStreamingAsync(_device, _channel, _intervalMs).ConfigureAwait(false);
        if (!result.IsSuccess)
            await StopAsync().ConfigureAwait(false);
        return result;
    }

    private void OnSampled(SmuSample sample)
    {
        if (!string.Equals(sample.Device, _device, StringComparison.OrdinalIgnoreCase))
            return;
        if (sample.Channel != _channel)
            return;
        Publish(_valueType == FeedbackValue.Voltage ? sample.Voltage : sample.Current);
    }

    public override async Task StopAsync()
    {
        if (_subscribed)
        {
            _engine.Smu.Sampled -= OnSampled;
            _subscribed = false;
        }
        await _engine.Smu.StopStreamingAsync(_device, _channel).ConfigureAwait(false);
    }
}
