using System.Globalization;
using System.Text.Json;
using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Scanning;

namespace Horizon.Workflow;

/// <summary>
/// Maps a <see cref="WorkflowStep"/> to a typed <see cref="IHorizonEngine"/>
/// facet call — no reflection. Control-flow ops (<c>flow.loopStart/End</c>,
/// <c>flow.if/else/endif</c>, <c>flow.stop</c>, <c>flow.subProgram</c>,
/// <c>flow.groupStart/End</c>) are handled by the <see cref="WorkflowRunner"/>,
/// not here.
/// </summary>
internal sealed class StepDispatcher
{
    private readonly IHorizonEngine _engine;
    // (device, position, overrides, ct) — overrides carry the optional velocity / camera / exposure.
    private readonly Func<string, string, MoveOverrides, CancellationToken, Task<Result>> _moveToNamed;
    private readonly Func<string, string, NamedPosition?> _getPosition;
    private readonly Func<string, string, MotionPosition, double, Result> _savePosition;
    private readonly DeviceLinkStore _deviceLinks;
    private readonly Action<LogLevel, string> _log;

    public StepDispatcher(
        IHorizonEngine engine,
        Func<string, string, MoveOverrides, CancellationToken, Task<Result>> moveToNamed,
        Func<string, string, NamedPosition?> getPosition,
        Func<string, string, MotionPosition, double, Result> savePosition,
        DeviceLinkStore deviceLinks,
        Action<LogLevel, string> log)
    {
        _engine = engine;
        _moveToNamed = moveToNamed;
        _getPosition = getPosition;
        _savePosition = savePosition;
        _deviceLinks = deviceLinks;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        switch (step.Op.ToLowerInvariant())
        {
            case "motion.setvelocity":
                return await _engine.Motion.SetDeviceVelocityAsync(step.Device, Num(step, "speed", ctx));

            case "motion.movetoposition":
                return await _moveToNamed(step.Device, Str(step, "position", ctx), MoveOverridesOf(step, ctx), ct);

            case "motion.parallelmove":
            {
                var tasks = new List<Task<Result>>();
                for (int i = 1; ; i++)
                {
                    if (!step.Parameters.TryGetValue($"device{i}", out var dev) || string.IsNullOrWhiteSpace(dev))
                        break;
                    var posName = step.Parameters.GetValueOrDefault($"position{i}", string.Empty);
                    tasks.Add(_moveToNamed(dev, posName, new MoveOverrides(), ct));
                }
                if (tasks.Count == 0) return Result.Ok();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.All(r => r.IsSuccess) ? Result.Ok() : results.First(r => !r.IsSuccess);
            }

            case "motion.moverelative":
            {
                var distance = Num(step, "distance", ctx);
                if (distance == 0)
                    return Result.Fail(ErrorCode.InvalidParameter,
                        "Move Relative distance must be non-zero.");

                var ov = MoveOverridesOf(step, ctx);

                // Optional per-step velocity (mm/s); 0 = leave the device's current
                // velocity. Clamped to the controller type's range as a safety net.
                var velocity = MotionVelocityLimits.Clamp(
                    ov.Velocity, _engine.Motion.GetDevice(step.Device)?.ControllerType);
                if (velocity > 0)
                {
                    var setVel = await _engine.Motion.SetDeviceVelocityAsync(step.Device, velocity);
                    if (!setVel.IsSuccess) return setVel;
                }

                // Start the move, then apply the camera exposure WHILE it travels, then
                // wait for it to settle — the ~300 ms exposure overlaps the move.
                var moveTask = _engine.Motion.MoveRelativeAsync(step.Device, Str(step, "axis", ctx), distance, ct);
                await MoveExposure.ApplyAsync(_engine.Camera, ov.Camera, ov.ExposureUs, _log).ConfigureAwait(false);
                return await moveTask.ConfigureAwait(false);
            }

            case "motion.stop":
                return await _engine.Motion.StopAsync(step.Device);

            case "motion.readposition":
            {
                // Reads the device's current position into context variables
                // '{prefix}_x/_y/_z' (and '_u/_v/_w' when 'saveUvw'). Ported
                // from bobcat's "Read Position".
                var r = await _engine.Motion.GetPositionAsync(step.Device);
                if (!r.IsSuccess) return Result.Fail(r.Error, r.Message);
                var p = r.Value;
                var prefix = Has(step, "prefix") ? Str(step, "prefix", ctx) : step.Device;
                ctx.Set($"{prefix}_x", p.X);
                ctx.Set($"{prefix}_y", p.Y);
                ctx.Set($"{prefix}_z", p.Z);
                if (Bool(step, "saveUvw", ctx))
                {
                    ctx.Set($"{prefix}_u", p.U);
                    ctx.Set($"{prefix}_v", p.V);
                    ctx.Set($"{prefix}_w", p.W);
                }
                _log(LogLevel.Info, $"{step.Device} position -> {prefix}_x/_y/_z = " +
                    $"{p.X:F4}, {p.Y:F4}, {p.Z:F4}");
                return Result.Ok();
            }

            case "motion.updateposition":
            {
                // Overwrites a named position with the device's current
                // coordinates. Ported from bobcat's "Update Position All".
                var name = Str(step, "position", ctx);
                if (string.IsNullOrWhiteSpace(name))
                    return Result.Fail(ErrorCode.InvalidParameter, "Update Position: no position name.");
                var cur = await _engine.Motion.GetPositionAsync(step.Device);
                if (!cur.IsSuccess) return Result.Fail(cur.Error, cur.Message);
                var velocity = _getPosition(step.Device, name)?.Velocity ?? 0.0;
                var saved = _savePosition(step.Device, name, cur.Value, velocity);
                if (saved.IsSuccess)
                    _log(LogLevel.Info, $"Updated position '{name}' on {step.Device} <- current coordinates.");
                return saved;
            }

            case "motion.updatepositionaxis":
            {
                // Replaces one axis of a named position with a context value.
                // Ported from bobcat's "Update Position Axis".
                var name = Str(step, "position", ctx);
                var existing = _getPosition(step.Device, name);
                if (existing == null)
                    return Result.Fail(ErrorCode.FileNotFound,
                        $"Update Position Axis: position '{name}' not found on {step.Device}.");
                var axis = Str(step, "axis", ctx).Trim().ToUpperInvariant();
                if (axis is not ("X" or "Y" or "Z" or "U" or "V" or "W"))
                    return Result.Fail(ErrorCode.InvalidParameter,
                        $"Update Position Axis: unknown axis '{axis}'.");
                var v = ctx.GetNumeric(Str(step, "contextKey", ctx));
                var c = existing.Coordinates;
                var updated = axis switch
                {
                    "X" => c with { X = v },
                    "Y" => c with { Y = v },
                    "Z" => c with { Z = v },
                    "U" => c with { U = v },
                    "V" => c with { V = v },
                    _   => c with { W = v },
                };
                var saved = _savePosition(step.Device, name, updated, existing.Velocity);
                if (saved.IsSuccess)
                    _log(LogLevel.Info, $"Updated position '{name}' axis {axis} = {v} on {step.Device}.");
                return saved;
            }

            case "motion.registerdevicelink":
            {
                // Captures the offset between two devices' current positions.
                // Ported from bobcat's "Register Device Link".
                var primary = Str(step, "primary", ctx);
                var linked = Str(step, "linked", ctx);
                var p = await _engine.Motion.GetPositionAsync(primary);
                if (!p.IsSuccess) return Result.Fail(p.Error, $"register device link — primary: {p.Message}");
                var l = await _engine.Motion.GetPositionAsync(linked);
                if (!l.IsSuccess) return Result.Fail(l.Error, $"register device link — linked: {l.Message}");
                var offset = new MotionPosition(
                    l.Value.X - p.Value.X, l.Value.Y - p.Value.Y, l.Value.Z - p.Value.Z,
                    l.Value.U - p.Value.U, l.Value.V - p.Value.V, l.Value.W - p.Value.W);
                _deviceLinks.Register(new DeviceLink(primary, linked, offset));
                _log(LogLevel.Info, $"Device link registered: {linked} = {primary} + " +
                    $"({offset.X:F3}, {offset.Y:F3}, {offset.Z:F3}).");
                return Result.Ok();
            }

            case "motion.compensatedevicelink":
            {
                // Moves the linked device to restore its registered offset from
                // the primary. Ported from bobcat's "Compensate Device Link".
                var primary = Str(step, "primary", ctx);
                var linked = Str(step, "linked", ctx);
                var link = _deviceLinks.Find(primary, linked);
                if (link == null)
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"compensate device link: no link registered for {primary} -> {linked}.");
                var p = await _engine.Motion.GetPositionAsync(primary);
                if (!p.IsSuccess) return Result.Fail(p.Error, $"compensate device link — primary: {p.Message}");
                var cur = await _engine.Motion.GetPositionAsync(linked);
                if (!cur.IsSuccess) return Result.Fail(cur.Error, $"compensate device link — linked: {cur.Message}");
                // 'axes' selects which translational axes to correct; empty = XYZ.
                var axes = Str(step, "axes", ctx).ToUpperInvariant();
                bool any = axes.Length > 0;
                bool ax = !any || axes.Contains('X');
                bool ay = !any || axes.Contains('Y');
                bool az = !any || axes.Contains('Z');
                var target = new MotionPosition(
                    ax ? p.Value.X + link.Offset.X : cur.Value.X,
                    ay ? p.Value.Y + link.Offset.Y : cur.Value.Y,
                    az ? p.Value.Z + link.Offset.Z : cur.Value.Z,
                    cur.Value.U, cur.Value.V, cur.Value.W);
                _log(LogLevel.Info, $"Compensating {linked} to track {primary} (axes {(any ? axes : "XYZ")}).");
                return await _engine.Motion.MoveToPositionAsync(linked, target, ct);
            }

            case "scan.hexapod":
            {
                // Coarse-to-fine hexapod beam-alignment scan — exposes
                // HexapodScanEngine (see docs/hexapod-scanning.md) as a step.
                var feedback = new FeedbackDescriptor
                {
                    Kind = ParseFeedbackKind(Str(step, "feedbackKind", ctx)),
                    Device = Str(step, "feedbackDevice", ctx),
                    Channel = Has(step, "feedbackChannel") ? (int)Num(step, "feedbackChannel", ctx) : 1,
                    ValueType = Str(step, "feedbackValue", ctx).Trim()
                        .Equals("voltage", StringComparison.OrdinalIgnoreCase)
                        ? FeedbackValue.Voltage : FeedbackValue.Current,
                    IntervalMs = Has(step, "feedbackIntervalMs") ? (int)Num(step, "feedbackIntervalMs", ctx) : 50,
                };

                var p = ScanParameters.ForMode(ParseScanMode(Str(step, "mode", ctx)));
                if (Has(step, "axes"))
                {
                    var axes = SplitAxes(Str(step, "axes", ctx));
                    if (axes.Length > 0) p = p with { AxesToScan = axes };
                }
                if (Has(step, "stepSizesUm"))
                {
                    var steps = ParseStepSizesMm(Str(step, "stepSizesUm", ctx));
                    if (steps.Length > 0) p = p with { StepSizes = steps };
                }
                if (Has(step, "settleMs"))
                    p = p with { MotionSettleMs = (int)Num(step, "settleMs", ctx) };
                if (Has(step, "velocity"))
                    p = p with { ScanVelocityMmPerSec = Num(step, "velocity", ctx) };

                var result = await new HexapodScanEngine(_engine, _log)
                    .RunAsync(step.Device, feedback, p, progress: null, ct).ConfigureAwait(false);

                var captureAs = Str(step, "captureAs", ctx);
                if (!string.IsNullOrWhiteSpace(captureAs))
                {
                    ctx.Set($"{captureAs}_peak", result.Peak.Value);
                    ctx.Set($"{captureAs}_baseline", result.Baseline.Value);
                    ctx.Set($"{captureAs}_snr", result.PeakSnr);
                    ctx.Set($"{captureAs}_significant", result.PeakIsSignificant ? "1" : "0");
                    ctx.Set($"{captureAs}_state", result.FinalState.ToString());
                }

                // A scan that ran (even one that found only noise) is a step
                // success — the recipe branches on '{captureAs}_significant'.
                // Only a setup/hardware error fails the step.
                return result.FinalState == ScanState.Error
                    ? Result.Fail(ErrorCode.OperationFailed, result.Message)
                    : Result.Ok(result.Message);
            }

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
                return await _engine.IO.SetOutputAsync(step.Device, (int)Num(step, "pin", ctx), Bool(step, "state", ctx), ct);

            case "io.setoutputbyname":
                return await _engine.IO.SetOutputByNameAsync(
                    step.Device, Str(step, "pin", ctx), Bool(step, "state", ctx), ct);

            case "io.toggleoutput":
            {
                // Full toggle cycle on a named output, holding 'dwellMs' (default 150)
                // after EACH transition: start state → dwell → opposite → dwell → back
                // to start → dwell. 'startState' is the resting state the pin ends in.
                // Ported/extended from bobcat's "Toggle Output".
                var pin = Str(step, "pin", ctx);
                var start = Bool(step, "startState", ctx);
                var dwellMs = Has(step, "dwellMs") ? (int)Num(step, "dwellMs", ctx) : 150;
                foreach (var state in new[] { start, !start, start })
                {
                    var r = await _engine.IO.SetOutputByNameAsync(step.Device, pin, state, ct);
                    if (!r.IsSuccess) return r;
                    if (dwellMs > 0) await Task.Delay(dwellMs, ct).ConfigureAwait(false);
                }
                return Result.Ok();
            }

            case "pneumatic.retract":
                return await _engine.Pneumatic.RetractAndWaitAsync(step.Device, ct);

            case "pneumatic.extend":
                return await _engine.Pneumatic.ExtendAndWaitAsync(step.Device, ct);

            case "pneumatic.retractall":
            {
                // Retract every configured slide at once and block until each one
                // confirms its retracted sensor (or times out). There is no extend-all.
                var tasks = _engine.Pneumatic.SlideNames
                    .Select(slide => _engine.Pneumatic.RetractAndWaitAsync(slide, ct))
                    .ToList();
                if (tasks.Count == 0) return Result.Ok();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.All(r => r.IsSuccess) ? Result.Ok() : results.First(r => !r.IsSuccess);
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
                // Starts fast streaming (NPLC 0.1) — the same path the Machine
                // page's Measurement Start button drives. The engine's normal
                // slow poll always runs; this is the high-rate mode.
                return await _engine.Measurement.StartStreamingAsync(step.Device);

            case "measurement.stoppolling":
                return await _engine.Measurement.StopStreamingAsync(step.Device);

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

            case "psu.readvoltage":
            case "psu.readcurrent":
            {
                await _engine.PowerSupply.RefreshAsync();
                var state = _engine.PowerSupply.GetState(step.Device);
                if (!state.Valid)
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"Power supply '{step.Device}' has no readings yet.");
                var channel = PsuChannel(step, ctx);
                var pch = state.Channels.FirstOrDefault(c => c.Channel == channel);
                if (pch == null)
                    return Result.Fail(ErrorCode.InvalidParameter,
                        $"Channel {channel} not found for '{step.Device}'.");
                var isCurrent = step.Op.EndsWith("current", StringComparison.OrdinalIgnoreCase);
                var value = isCurrent ? pch.Current : pch.Voltage;
                var captureAs = Str(step, "captureAs", ctx);
                if (!string.IsNullOrWhiteSpace(captureAs)) ctx.Set(captureAs, value);
                _log(LogLevel.Info, $"{step.Device} CH{channel} {(isCurrent ? "current" : "voltage")} = {value}" +
                    (string.IsNullOrWhiteSpace(captureAs) ? "" : $" -> {captureAs}"));
                return Result.Ok();
            }

            case "lighting.connect":
                return await _engine.Lighting.ConnectAsync(step.Device, ct);

            case "lighting.setintensity":
                return await _engine.Lighting.SetChannelIntensityAsync(
                    step.Device, (int)Num(step, "channel", ctx), (int)Num(step, "intensity", ctx));

            case "lighting.setnamedchannel":
                return await _engine.Lighting.SetNamedChannelAsync(
                    step.Device, Str(step, "channel", ctx), (int)Num(step, "intensity", ctx));

            case "lighting.setall":
                return await _engine.Lighting.SetAllAsync(step.Device, (int)Num(step, "intensity", ctx));

            case "lighting.setgroup":
                return await _engine.Lighting.SetGroupAsync(
                    step.Device, Str(step, "group", ctx), (int)Num(step, "intensity", ctx));

            case "lighting.configuregroup":
            {
                // Per-colour intensities for a light group; channels of an
                // unlisted colour are turned off by the facet.
                var byColor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["white"] = (int)Num(step, "white_intensity", ctx),
                    ["red"]   = (int)Num(step, "red_intensity", ctx),
                    ["green"] = (int)Num(step, "green_intensity", ctx),
                    ["blue"]  = (int)Num(step, "blue_intensity", ctx),
                };
                return await _engine.Lighting.ConfigureGroupAsync(step.Device, Str(step, "group", ctx), byColor);
            }

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

            case "tec.readtemperature":
            {
                await _engine.Tec.RefreshAsync();
                var state = _engine.Tec.GetState(step.Device);
                if (!state.Valid)
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"TEC '{step.Device}' has no readings yet.");
                if (double.IsNaN(state.Temperature))
                    return Result.Fail(ErrorCode.OperationFailed,
                        $"TEC '{step.Device}' has no temperature reading (check thermistor).");
                var captureAs = Str(step, "captureAs", ctx);
                if (!string.IsNullOrWhiteSpace(captureAs)) ctx.Set(captureAs, state.Temperature);
                _log(LogLevel.Info, $"{step.Device} temperature = {state.Temperature:F2} C" +
                    (string.IsNullOrWhiteSpace(captureAs) ? "" : $" -> {captureAs}"));
                return Result.Ok();
            }

            case "tec.waitfortemperature":
            {
                var target = Num(step, "celsius", ctx);
                var tol = Has(step, "toleranceC") ? Num(step, "toleranceC", ctx) : 1.0;
                var timeoutMs = Has(step, "timeoutMs") ? (int)Num(step, "timeoutMs", ctx) : 60000;
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    await _engine.Tec.RefreshAsync();
                    var t = _engine.Tec.GetState(step.Device).Temperature;
                    if (!double.IsNaN(t) && Math.Abs(t - target) <= tol)
                    {
                        _log(LogLevel.Info, $"{step.Device} reached {t:F2} C (target {target:F2}).");
                        return Result.Ok();
                    }
                    if (DateTime.UtcNow >= deadline)
                        return Result.Fail(ErrorCode.OperationFailed,
                            $"TEC '{step.Device}' did not reach {target:F2} C within {timeoutMs} ms (last {t:F2} C).");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }

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

            case "smu.readcurrent":
            case "smu.readvoltage":
            {
                var channel = Has(step, "channel") ? (int)Num(step, "channel", ctx) : 1;
                var r = await _engine.Smu.ReadMeasurementAsync(step.Device, channel);
                if (!r.IsSuccess) return Result.Fail(r.Error, r.Message);
                var isCurrent = step.Op.EndsWith("current", StringComparison.OrdinalIgnoreCase);
                var value = isCurrent ? r.Value.Current : r.Value.Voltage;
                var captureAs = Str(step, "captureAs", ctx);
                if (!string.IsNullOrWhiteSpace(captureAs)) ctx.Set(captureAs, value);
                _log(LogLevel.Info, $"{step.Device} CH{r.Value.Channel} {(isCurrent ? "current" : "voltage")} = {value}" +
                    (string.IsNullOrWhiteSpace(captureAs) ? "" : $" -> {captureAs}"));
                return Result.Ok();
            }

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

            case "var.clear":
                ctx.Clear();
                _log(LogLevel.Info, "Context variables cleared.");
                return Result.Ok();

            case "dut.record":
                RecordDut(step, ctx, Str(step, "key", ctx), Str(step, "value", ctx));
                return Result.Ok();

            case "dut.recordfromcontext":
                RecordDut(step, ctx, Str(step, "key", ctx), ctx.GetString(Str(step, "from", ctx)));
                return Result.Ok();

            case "dut.recordgroup":
            {
                // Records every context variable whose name starts with 'prefix'
                // as one grouped Object entry (a JSON map). Ported from bobcat's
                // "DUT Bulk Insert from Context".
                if (ctx.Dut == null)
                {
                    _log(LogLevel.Warning, "dut.recordGroup skipped — no DUT serial for this run.");
                    return Result.Ok();
                }
                var group = Str(step, "group", ctx);
                var prefix = Str(step, "prefix", ctx);
                var members = ctx.Snapshot()
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                ctx.Dut.Entries.Add(new DutEntry(
                    DateTime.UtcNow, group, JsonSerializer.Serialize(members), DutValueType.Object));
                _log(LogLevel.Info,
                    $"DUT record group '{group}' <- {members.Count} value(s) with prefix '{prefix}'");
                return Result.Ok();
            }

            default:
                return Result.Fail(ErrorCode.NotSupported, $"Unknown step op: '{step.Op}'");
        }
    }

    // --- parameter resolution: literal | var:NAME | expr:EXPRESSION ---

    /// <summary>
    /// Appends a value to the run's DUT record. A no-op (with a warning) when the
    /// run has no DUT serial — recipes can run unattached to a unit.
    /// </summary>
    private void RecordDut(WorkflowStep step, WorkflowContext ctx, string key, string value)
    {
        if (ctx.Dut == null)
        {
            _log(LogLevel.Warning, $"dut.record '{key}' skipped — no DUT serial for this run.");
            return;
        }
        var type = ResolveDutType(step.Parameters.GetValueOrDefault("type"), value);
        ctx.Dut.Entries.Add(new DutEntry(DateTime.UtcNow, key, value, type));
        _log(LogLevel.Info, $"DUT record '{key}' = {value} ({type})");
    }

    /// <summary>
    /// Resolves the <see cref="DutValueType"/> for a recorded value — honours an
    /// explicit <c>type</c> param, otherwise infers integer / double / string.
    /// </summary>
    private static DutValueType ResolveDutType(string? declared, string value)
    {
        switch ((declared ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "string":  return DutValueType.String;
            case "double":  return DutValueType.Double;
            case "integer": return DutValueType.Integer;
            case "object":  return DutValueType.Object;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return DutValueType.Integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return DutValueType.Double;
        return DutValueType.String;
    }

    private static FeedbackKind ParseFeedbackKind(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "measurement" => FeedbackKind.Measurement,
        "powersupply" or "power supply" or "psu" => FeedbackKind.PowerSupply,
        _ => FeedbackKind.Smu,
    };

    private static ScanMode ParseScanMode(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "fine" => ScanMode.Fine,
        "ultrafine" or "ultra fine" or "ultra-fine" => ScanMode.UltraFine,
        _ => ScanMode.Coarse,
    };

    /// <summary>Splits an axis list — "Z,Y,X" or concatenated "ZYX".</summary>
    private static string[] SplitAxes(string raw)
        => raw.Contains(',')
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(a => a.ToUpperInvariant()).ToArray()
            : raw.Where(c => !char.IsWhiteSpace(c)).Select(c => char.ToUpperInvariant(c).ToString()).ToArray();

    /// <summary>Parses a comma-separated step list in µm into millimetres.</summary>
    private static double[] ParseStepSizesMm(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var um)
                ? um / 1000.0 : 0.0)
            .Where(mm => mm > 0)
            .ToArray();

    /// <summary>True if the step supplies a non-empty value for <paramref name="key"/>.</summary>
    private static bool Has(WorkflowStep step, string key)
        => step.Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v);

    /// <summary>Power-supply channel (1-based) — defaults to channel 1 when omitted.</summary>
    private static int PsuChannel(WorkflowStep step, WorkflowContext ctx)
        => Has(step, "channel") ? (int)Num(step, "channel", ctx) : 1;

    /// <summary>Reads the optional velocity / camera / exposure params of a move step.</summary>
    private static MoveOverrides MoveOverridesOf(WorkflowStep step, WorkflowContext ctx)
    {
        var camera = Str(step, "camera", ctx);
        var exposure = Num(step, "exposure", ctx);
        return new MoveOverrides(
            Num(step, "velocity", ctx),
            string.IsNullOrWhiteSpace(camera) ? null : camera,
            exposure > 0 ? exposure : null);
    }

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
