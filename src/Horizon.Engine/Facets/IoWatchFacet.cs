using Horizon.Common;
using Horizon.IO;
using Horizon.IO.Models;

namespace Horizon.Engine;

/// <summary>
/// IO-watch facet — wraps the <see cref="IoWatchManager"/>. Exposes read state,
/// the bindings, and a fired event; the poll/drive loop is owned by the manager.
/// </summary>
internal sealed class IoWatchFacet : IIoWatchFacet, IDisposable
{
    private readonly IoWatchManager _watch;
    private readonly Action<HardwareStatusChanged> _emitStatus;
    private readonly Action<LogLevel, string> _log;

    public IoWatchFacet(
        IoWatchManager watch,
        Action<HardwareStatusChanged> emitStatus,
        Action<LogLevel, string> log)
    {
        _watch = watch;
        _emitStatus = emitStatus;
        _log = log;
        _watch.BindingFired += OnBindingFired;
    }

    public IoWatchState GetState() => new(
        _watch.IsRunning, _watch.Enabled, _watch.PollIntervalMs, _watch.Bindings.Count);

    public IReadOnlyList<IoBindingInfo> Bindings =>
        _watch.Bindings.Select(b => new IoBindingInfo(
            b.Name,
            $"{b.Input.Device}.{b.Input.PinName}",
            $"{b.Output.Device}.{b.Output.PinName}",
            b.Trigger.ToString(),
            b.DebounceMs,
            b.Enabled)).ToList();

    public event Action<IoBindingFired>? Fired;

    public void Dispose() => _watch.BindingFired -= OnBindingFired;

    private void OnBindingFired(IoBinding b, bool state)
    {
        _log(LogLevel.Info, $"IO watch: {b.Name} → {b.Output.Device}.{b.Output.PinName} = {(state ? "ON" : "OFF")}");
        Fired?.Invoke(new IoBindingFired(b.Name, $"{b.Output.Device}.{b.Output.PinName}", state));
        _emitStatus(new HardwareStatusChanged("IoWatch", b.Name));
    }
}
