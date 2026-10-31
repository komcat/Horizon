using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// Shared low-level dispenser-head primitives used by the camera→needle calibration, the
/// dispensing routines and <c>needle.approach</c>: lower / raise the head, pulse the glue
/// valve, a slow relative Z move — plus the fail-safe that de-energizes the shot and
/// retracts on abort. Gantry moves at a set velocity live in <see cref="NeedleGantry"/>.
/// </summary>
internal sealed class DispenseHead
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;
    private readonly string _tag;

    /// <param name="tag">Log-line prefix, e.g. "Camera-needle" or "Dispense".</param>
    public DispenseHead(IHorizonEngine engine, Action<LogLevel, string> log, string tag)
    {
        _engine = engine;
        _log = log;
        _tag = tag;
    }

    /// <summary>Lowers the dispenser head (sensor-confirmed).</summary>
    public async Task<Result> ExtendAsync(string slide, CancellationToken ct)
    {
        _log(LogLevel.Info, $"{_tag}: extending dispenser head '{slide}'.");
        var ext = await _engine.Pneumatic.ExtendAndWaitAsync(slide, ct).ConfigureAwait(false);
        return ext.IsSuccess ? Result.Ok() : Result.Fail(ext.Error, $"Dispenser extend: {ext.Message}");
    }

    /// <summary>Raises the dispenser head (sensor-confirmed).</summary>
    public async Task<Result> RetractAsync(string slide, CancellationToken ct)
    {
        var ret = await _engine.Pneumatic.RetractAndWaitAsync(slide, ct).ConfigureAwait(false);
        return ret.IsSuccess ? Result.Ok() : Result.Fail(ret.Error, $"Dispenser retract: {ret.Message}");
    }

    /// <summary>
    /// Opens the glue valve for <paramref name="shotMs"/> ms — the shot size; there is no pressure
    /// control in this loop, the external regulator sets the flow. One call rather than
    /// on / delay / off so the valve closes <b>even when a Stop interrupts the wait</b>: a Stop that
    /// left the shot output energised would keep pumping.
    /// </summary>
    public async Task<Result> PulseShotAsync(string shotIoDevice, string shotPin, int shotMs, CancellationToken ct)
    {
        _log(LogLevel.Info, $"{_tag}: shot {shotIoDevice}/{shotPin} {shotMs} ms.");
        var on = await _engine.IO.SetOutputByNameAsync(shotIoDevice, shotPin, true, ct).ConfigureAwait(false);
        if (!on.IsSuccess) return Result.Fail(on.Error, $"Dispenser shot ON ({shotIoDevice}/{shotPin}): {on.Message}");
        try
        {
            if (shotMs > 0) await Task.Delay(shotMs, ct).ConfigureAwait(false);
        }
        finally
        {
            var off = await _engine.IO.SetOutputByNameAsync(shotIoDevice, shotPin, false, CancellationToken.None).ConfigureAwait(false);
            if (!off.IsSuccess) _log(LogLevel.Error, $"{_tag}: shot OFF {shotIoDevice}/{shotPin} FAILED: {off.Message}");
        }
        return Result.Ok();
    }

    /// <summary>Relative Z move at a slow velocity, restoring the prior axis velocity afterward.</summary>
    public async Task<Result> SlowZMoveAsync(string device, double dz, double slowVel, CancellationToken ct)
    {
        var prev = await _engine.Motion.GetVelocityAsync(device, "Z").ConfigureAwait(false);
        try
        {
            await _engine.Motion.SetVelocityAsync(device, "Z", slowVel).ConfigureAwait(false);
            return await _engine.Motion.MoveRelativeAsync(device, "Z", dz, ct).ConfigureAwait(false);
        }
        finally
        {
            if (prev.IsSuccess)
                await _engine.Motion.SetVelocityAsync(device, "Z", prev.Value).ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort de-energize of the shot output.</summary>
    public async Task ShotOffAsync(string shotIoDevice, string shotPin)
    {
        try
        {
            await _engine.IO.SetOutputByNameAsync(shotIoDevice, shotPin, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"{_tag} fail-safe shot-off failed: {ex.Message}");
        }
    }

    /// <summary>On abort, de-energize the shot output and raise the dispenser head. Best-effort.</summary>
    public async Task FailSafeAsync(string shotIoDevice, string shotPin, string slide)
    {
        await ShotOffAsync(shotIoDevice, shotPin).ConfigureAwait(false);
        try
        {
            await _engine.Pneumatic.RetractAndWaitAsync(slide, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"{_tag} fail-safe retract failed: {ex.Message}");
        }
    }
}
