using System.Globalization;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// <c>vision.detectObject</c> — the self-contained detect block (see
/// <c>docs/detect-object-design.md</c>). Its camera position (<c>posX/posY/posZ</c>) and vision
/// recipe (<c>recipe</c>) live in the block's own parameters. Results go to
/// <c>{captureAs}_found / _x / _y / _p / _score / _px / _py / _manual</c>.
/// </summary>
internal sealed partial class StepDispatcher
{
    private async Task<Result> DetectObjectAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        var captureAs = Has(step, "captureAs") ? Str(step, "captureAs", ctx).Trim() : "obj";
        if (captureAs.Length == 0) captureAs = "obj";

        // Strict numbers: a typo must fail the step, never silently become 0 on a motion block.
        var numbers = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "posX", "posY", "posZ", "velocity", "settleMs", "maxMoveMm", "offsetX", "offsetY", "offsetZ" })
        {
            if (!Has(step, key)) { numbers[key] = null; continue; }
            var text = Str(step, key, ctx);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                return Result.Fail(ErrorCode.InvalidParameter, $"vision.detectObject: '{key}' is not a number ('{text}').");
            numbers[key] = d;
        }

        int taught = new[] { "posX", "posY", "posZ" }.Count(k => numbers[k].HasValue);
        if (taught is 1 or 2)
            return Result.Fail(ErrorCode.InvalidParameter, "vision.detectObject: the block position is incomplete — teach it again or clear it.");

        var request = new DetectObjectRequest
        {
            Label = string.IsNullOrWhiteSpace(step.Name) ? "Detect Object" : step.Name,
            PosX = numbers["posX"],
            PosY = numbers["posY"],
            PosZ = numbers["posZ"],
            Recipe = step.Parameters.GetValueOrDefault("recipe", string.Empty),
            Velocity = numbers["velocity"] ?? 0,
            SettleMs = (int)Math.Clamp(numbers["settleMs"] ?? 300, 0, 60_000),
            RequireMatch = !Has(step, "requireMatch") || Bool(step, "requireMatch", ctx),
            AskOperator = Bool(step, "askOperator", ctx),
            MoveToResult = Bool(step, "moveToResult", ctx),
            UseX = !Has(step, "useX") || Bool(step, "useX", ctx),
            UseY = !Has(step, "useY") || Bool(step, "useY", ctx),
            UseP = !Has(step, "useP") || Bool(step, "useP", ctx),
            MaxMoveMm = numbers["maxMoveMm"] ?? DetectObjectRequest.HardMaxMoveMm,
            OffsetX = numbers["offsetX"] ?? 0,
            OffsetY = numbers["offsetY"] ?? 0,
            OffsetZ = numbers["offsetZ"] ?? 0,
        };

        // Not found until the routine says otherwise — a failure before detection must not leave
        // an earlier run's result in the variables.
        ctx.Set($"{captureAs}_found", "0");

        var r = await _runDetectObject(request, obj => Capture(ctx, captureAs, obj), ct).ConfigureAwait(false);
        return r.IsSuccess ? Result.Ok() : Result.Fail(r.Error, r.Message);
    }

    private void Capture(WorkflowContext ctx, string captureAs, DetectedObject obj)
    {
        ctx.Set($"{captureAs}_found", obj.Found ? "1" : "0");
        if (!obj.Found) return;
        ctx.Set($"{captureAs}_x", obj.X);
        ctx.Set($"{captureAs}_y", obj.Y);
        ctx.Set($"{captureAs}_p", obj.P);
        ctx.Set($"{captureAs}_score", obj.Score);
        ctx.Set($"{captureAs}_px", obj.Px);
        ctx.Set($"{captureAs}_py", obj.Py);
        ctx.Set($"{captureAs}_manual", obj.Manual ? "1" : "0");
        _log(LogLevel.Info, $"vision.detectObject: X {obj.X:F4}  Y {obj.Y:F4}  P {obj.P:F2}° -> {captureAs}_*");
    }
}
