using System.Globalization;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Maps a <see cref="WorkflowStep"/> to a typed <see cref="IHorizonEngine"/>
/// facet call — no reflection. Control-flow ops (<c>flow.loopStart/End</c>) are
/// handled by the <see cref="WorkflowRunner"/>, not here.
/// </summary>
internal sealed class StepDispatcher
{
    private readonly IHorizonEngine _engine;
    private readonly Func<string, string, CancellationToken, Task<Result>> _moveToNamed;
    private readonly Action<LogLevel, string> _log;

    public StepDispatcher(
        IHorizonEngine engine,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed,
        Action<LogLevel, string> log)
    {
        _engine = engine;
        _moveToNamed = moveToNamed;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        switch (step.Op.ToLowerInvariant())
        {
            case "motion.setvelocity":
                return await _engine.Motion.SetDeviceVelocityAsync(step.Device, Num(step, "speed", ctx));

            case "motion.movetoposition":
                return await _moveToNamed(step.Device, Str(step, "position", ctx), ct);

            case "motion.parallelmove":
            {
                var tasks = new List<Task<Result>>();
                for (int i = 1; ; i++)
                {
                    if (!step.Parameters.TryGetValue($"device{i}", out var dev) || string.IsNullOrWhiteSpace(dev))
                        break;
                    var posName = step.Parameters.GetValueOrDefault($"position{i}", string.Empty);
                    tasks.Add(_moveToNamed(dev, posName, ct));
                }
                if (tasks.Count == 0) return Result.Ok();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.All(r => r.IsSuccess) ? Result.Ok() : results.First(r => !r.IsSuccess);
            }

            case "motion.moverelative":
                return await _engine.Motion.MoveRelativeAsync(
                    step.Device, Str(step, "axis", ctx), Num(step, "distance", ctx), ct);

            case "motion.stop":
                return await _engine.Motion.StopAsync(step.Device);

            case "camera.setexposure":
            {
                // Only fields named in Parameters are changed; the rest keep
                // the camera's current values. A single op (vs three) avoids a
                // poll race between consecutive per-field steps.
                if (!_engine.Camera.IsConnected(step.Device))
                    return Result.Fail(ErrorCode.CameraNotConnected,
                        $"Camera '{step.Device}' is not connected.");
                var ex = _engine.Camera.GetState(step.Device).Exposure;
                var applied = await _engine.Camera.ApplyExposureAsync(step.Device, new CameraExposure(
                    ExposureUs:   Has(step, "exposureUs")   ? Num(step, "exposureUs", ctx)   : ex.ExposureUs,
                    Gain:         Has(step, "gain")         ? Num(step, "gain", ctx)         : ex.Gain,
                    AutoExposure: Has(step, "autoExposure") ? Bool(step, "autoExposure", ctx): ex.AutoExposure,
                    AutoGain:     Has(step, "autoGain")     ? Bool(step, "autoGain", ctx)    : ex.AutoGain,
                    FrameRate:    Has(step, "frameRate")    ? Num(step, "frameRate", ctx)    : ex.FrameRate));
                // Connected but the apply still failed → a parameter the camera
                // rejected (e.g. exposure/gain out of range), not a connection.
                return applied.IsSuccess
                    ? applied
                    : Result.Fail(applied.Error,
                        $"{applied.Message} (camera connected — likely an out-of-range exposure/gain value).");
            }

            case "io.setoutput":
                return await _engine.IO.SetOutputAsync(step.Device, (int)Num(step, "pin", ctx), Bool(step, "state", ctx));

            case "io.setoutputbyname":
                return await _engine.IO.SetOutputByNameAsync(
                    step.Device, Str(step, "pin", ctx), Bool(step, "state", ctx), ct);

            case "pneumatic.retract":
                return await _engine.Pneumatic.RetractAndWaitAsync(step.Device, ct);

            case "pneumatic.extend":
                return await _engine.Pneumatic.ExtendAndWaitAsync(step.Device, ct);

            case "pneumatic.retractall":
            {
                foreach (var slide in _engine.Pneumatic.SlideNames)
                {
                    var r = await _engine.Pneumatic.RetractAsync(slide, ct);
                    if (!r.IsSuccess) return r;
                }
                return Result.Ok();
            }

            case "measurement.connect":
                return await _engine.Measurement.ConnectAsync(step.Device, ct);

            case "measurement.setvoltage":
                return await _engine.Measurement.SetVoltageAsync(step.Device, Num(step, "voltage", ctx));

            case "measurement.outputon":
                return await _engine.Measurement.SetVoltageEnabledAsync(step.Device, true);

            case "measurement.outputoff":
                return await _engine.Measurement.SetVoltageEnabledAsync(step.Device, false);

            case "measurement.startpolling":
            case "measurement.stoppolling":
                return Result.Ok();   // engine polls measurement continuously — no-op

            case "measurement.readcurrent":
            {
                // Reads a channel from the last poll and stores it in a context
                // variable named by 'captureAs'. Channels are 1-based here.
                var state = _engine.Measurement.GetState(step.Device);
                if (!state.Valid)
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"Measurement '{step.Device}' has no readings yet.");
                var ch = (int)Num(step, "channel", ctx);
                var idx = ch - 1;
                if (idx < 0 || idx >= state.Channels.Count)
                    return Result.Fail(ErrorCode.InvalidParameter,
                        $"Channel {ch} out of range for '{step.Device}'.");
                var value = state.Channels[idx];
                var captureAs = Str(step, "captureAs", ctx);
                if (!string.IsNullOrWhiteSpace(captureAs))
                    ctx.Set(captureAs, value.ToString(CultureInfo.InvariantCulture));
                _log(LogLevel.Info, $"{step.Device} CH{ch} = {value} {state.Unit}" +
                    (string.IsNullOrWhiteSpace(captureAs) ? "" : $" -> {captureAs}"));
                return Result.Ok();
            }

            case "measurement.read":
            {
                // Logs a channel's last polled reading (no capture).
                var state = _engine.Measurement.GetState(step.Device);
                var ch = (int)Num(step, "channel", ctx);
                var idx = ch - 1;
                if (!state.Valid || idx < 0 || idx >= state.Channels.Count)
                {
                    _log(LogLevel.Warning, $"{step.Device} CH{ch}: no reading available");
                    return Result.Ok();
                }
                _log(LogLevel.Info, $"{step.Device} CH{ch} = {state.Channels[idx]} {state.Unit}");
                return Result.Ok();
            }

            case "laser.connect":
                return await _engine.Laser.ConnectAsync(step.Device, ct);

            case "laser.settemperature":
            {
                var set = await _engine.Laser.SetTemperatureAsync(step.Device, Num(step, "temperature", ctx));
                if (!set.IsSuccess) return set;
                return Bool(step, "power", ctx)
                    ? await _engine.Laser.TecOnAsync(step.Device)
                    : await _engine.Laser.TecOffAsync(step.Device);
            }

            case "laser.setcurrent":
            {
                var set = await _engine.Laser.SetLaserCurrentAsync(step.Device, Num(step, "current", ctx));
                if (!set.IsSuccess) return set;
                return Bool(step, "power", ctx)
                    ? await _engine.Laser.LaserOnAsync(step.Device)
                    : await _engine.Laser.LaserOffAsync(step.Device);
            }

            case "psu.connect":
                return await _engine.PowerSupply.ConnectAsync(step.Device, ct);

            case "psu.setvoltage":
                return await _engine.PowerSupply.SetVoltageAsync(
                    step.Device, PsuChannel(step, ctx), Num(step, "volts", ctx));

            case "psu.setcurrentlimit":
                return await _engine.PowerSupply.SetCurrentLimitAsync(
                    step.Device, PsuChannel(step, ctx), Num(step, "amps", ctx));

            case "psu.outputon":
                return await _engine.PowerSupply.OutputOnAsync(step.Device, PsuChannel(step, ctx));

            case "psu.outputoff":
                return await _engine.PowerSupply.OutputOffAsync(step.Device, PsuChannel(step, ctx));

            case "lighting.connect":
                return await _engine.Lighting.ConnectAsync(step.Device, ct);

            case "lighting.setintensity":
                return await _engine.Lighting.SetChannelIntensityAsync(
                    step.Device, (int)Num(step, "channel", ctx), (int)Num(step, "intensity", ctx));

            case "tec.connect":
                return await _engine.Tec.ConnectAsync(step.Device, ct);

            case "tec.settemperature":
            {
                var set = await _engine.Tec.SetTemperatureAsync(step.Device, Num(step, "temperature", ctx));
                if (!set.IsSuccess) return set;
                return Bool(step, "power", ctx)
                    ? await _engine.Tec.TecOnAsync(step.Device)
                    : await _engine.Tec.TecOffAsync(step.Device);
            }

            case "tec.tecon":
                return await _engine.Tec.TecOnAsync(step.Device);

            case "tec.tecoff":
                return await _engine.Tec.TecOffAsync(step.Device);

            case "smu.connect":
                return await _engine.Smu.ConnectAsync(step.Device, ct);

            case "smu.setsourcevoltage":
                return await _engine.Smu.SetSourceVoltageAsync(step.Device, Num(step, "volts", ctx));

            case "smu.setsourcecurrent":
                return await _engine.Smu.SetSourceCurrentAsync(step.Device, Num(step, "amps", ctx));

            case "smu.setcompliance":
                return await _engine.Smu.SetComplianceAsync(step.Device, Num(step, "value", ctx));

            case "smu.outputon":
                return await _engine.Smu.OutputOnAsync(step.Device);

            case "smu.outputoff":
                return await _engine.Smu.OutputOffAsync(step.Device);

            case "flow.wait":
            {
                var ms = (int)Num(step, "ms", ctx);
                if (ms > 0) await Task.Delay(ms, ct).ConfigureAwait(false);
                return Result.Ok();
            }

            case "flow.comment":
                return Result.Ok();

            case "flow.log":
                _log(LogLevel.Info, Str(step, "message", ctx));
                return Result.Ok();

            case "var.set":
                ctx.Set(Str(step, "name", ctx), Str(step, "value", ctx));
                return Result.Ok();

            case "var.calculate":
                ctx.Set(Str(step, "name", ctx),
                    ExpressionEvaluator.Evaluate(step.Parameters.GetValueOrDefault("expression", "0"), ctx));
                return Result.Ok();

            default:
                return Result.Fail(ErrorCode.NotSupported, $"Unknown step op: '{step.Op}'");
        }
    }

    // --- parameter resolution: literal | var:NAME | expr:EXPRESSION ---

    /// <summary>True if the step supplies a non-empty value for <paramref name="key"/>.</summary>
    private static bool Has(WorkflowStep step, string key)
        => step.Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v);

    /// <summary>Power-supply channel (1-based) — defaults to channel 1 when omitted.</summary>
    private static int PsuChannel(WorkflowStep step, WorkflowContext ctx)
        => Has(step, "channel") ? (int)Num(step, "channel", ctx) : 1;

    private static string Str(WorkflowStep step, string key, WorkflowContext ctx)
    {
        var raw = step.Parameters.GetValueOrDefault(key, string.Empty);
        if (raw.StartsWith("var:", StringComparison.OrdinalIgnoreCase))
            return ctx.GetString(raw[4..]);
        if (raw.StartsWith("expr:", StringComparison.OrdinalIgnoreCase))
            return ExpressionEvaluator.Evaluate(raw[5..], ctx).ToString(CultureInfo.InvariantCulture);
        return raw;
    }

    private static double Num(WorkflowStep step, string key, WorkflowContext ctx)
        => double.TryParse(Str(step, key, ctx), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0.0;

    private static bool Bool(WorkflowStep step, string key, WorkflowContext ctx)
    {
        var v = Str(step, key, ctx).Trim().ToLowerInvariant();
        return v is "true" or "1" or "on" or "yes";
    }
}
