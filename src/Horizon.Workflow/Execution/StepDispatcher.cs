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

    public StepDispatcher(
        IHorizonEngine engine,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed)
    {
        _engine = engine;
        _moveToNamed = moveToNamed;
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

            case "io.setoutput":
                return await _engine.IO.SetOutputAsync(step.Device, (int)Num(step, "pin", ctx), Bool(step, "state", ctx));

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

            case "laser.connect":
                return await _engine.Laser.ConnectAsync(step.Device, ct);

            case "flow.wait":
            {
                var ms = (int)Num(step, "ms", ctx);
                if (ms > 0) await Task.Delay(ms, ct).ConfigureAwait(false);
                return Result.Ok();
            }

            case "flow.comment":
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
