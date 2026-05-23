using System.Linq;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Scanning.Feedback;

/// <summary>
/// Scan feedback from a power supply. The power-supply facet has no streaming,
/// so each read forces a fresh poll via <see cref="IPowerSupplyFacet.RefreshAsync"/>.
/// Coarse (~poll-rate limited) — best for slow alignments.
/// </summary>
internal sealed class PowerSupplyFeedbackSource : FeedbackSourceBase
{
    private readonly IHorizonEngine _engine;
    private readonly string _device;
    private readonly int _channel;   // 1-based
    private readonly FeedbackValue _valueType;
    private bool _subscribed;

    public PowerSupplyFeedbackSource(IHorizonEngine engine, FeedbackDescriptor descriptor)
    {
        _engine = engine;
        _device = descriptor.Device;
        _channel = descriptor.Channel;
        _valueType = descriptor.ValueType;
    }

    public override string Description =>
        $"Power supply {_device} CH{_channel} {_valueType.ToString().ToLowerInvariant()}";

    public override string Unit => _valueType == FeedbackValue.Voltage ? "V" : "A";

    public override Task<Result> StartAsync(CancellationToken ct)
    {
        if (_engine.PowerSupply.GetDevice(_device) == null)
            return Task.FromResult(Result.Fail(ErrorCode.OperationFailed, $"Power supply '{_device}' not found."));
        if (!_engine.PowerSupply.IsConnected(_device))
            return Task.FromResult(Result.Fail(ErrorCode.OperationFailed, $"Power supply '{_device}' is not connected."));

        _engine.PowerSupply.StateChanged += OnChanged;
        _subscribed = true;
        PublishFrom(_engine.PowerSupply.GetState(_device));
        return Task.FromResult(Result.Ok());
    }

    private void OnChanged(PowerSupplyStateChanged change)
    {
        if (string.Equals(change.Device, _device, StringComparison.OrdinalIgnoreCase))
            PublishFrom(change.State);
    }

    private void PublishFrom(PowerSupplyState state)
    {
        var channel = state.Channels.FirstOrDefault(c => c.Channel == _channel);
        if (channel != null)
            Publish(_valueType == FeedbackValue.Voltage ? channel.Voltage : channel.Current);
    }

    /// <summary>Forces a fresh poll, then returns the just-updated cached value.</summary>
    public override async Task<double> ReadAsync(CancellationToken ct)
    {
        await _engine.PowerSupply.RefreshAsync().ConfigureAwait(false);
        PublishFrom(_engine.PowerSupply.GetState(_device));
        return Latest();
    }

    public override Task StopAsync()
    {
        if (_subscribed)
        {
            _engine.PowerSupply.StateChanged -= OnChanged;
            _subscribed = false;
        }
        return Task.CompletedTask;
    }
}
