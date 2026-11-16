using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// <c>beacon.plant</c> / <c>beacon.remove</c> — virtual markers on the live views (see
/// <c>docs/beacon-design.md</c>). A beacon is a point in the camera device's axis values, kept in
/// the in-memory <see cref="BeaconRegistry"/>; it outlives the program that planted it.
/// </summary>
internal sealed partial class StepDispatcher
{
    private async Task<Result> PlantBeaconAsync(WorkflowStep step, WorkflowContext ctx)
    {
        bool show = !Has(step, "show") || Bool(step, "show", ctx);
        var r = await _beacons.PlantHereAsync(
            _engine, new AppSettingsStore().Current, Str(step, "name", ctx), show, _log).ConfigureAwait(false);
        return r.IsSuccess ? Result.Ok() : Result.Fail(r.Error, r.Message);
    }

    /// <summary>
    /// <c>object.register</c> — ties a gripped object's marker to its tool device. The point comes
    /// from a Detect Object result earlier in the program (<c>{from}_found / _x / _y</c>); from now
    /// on the marker moves with the tool. A result that found nothing fails the step: there is
    /// nothing to register.
    /// </summary>
    private async Task<Result> RegisterGrippedObjectAsync(WorkflowStep step, WorkflowContext ctx)
    {
        var from = Has(step, "from") ? Str(step, "from", ctx).Trim() : "obj";
        var tool = Str(step, "tool", ctx).Trim();
        if (tool.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "object.register: no tool device.");
        if (ctx.GetString($"{from}_found").Trim() != "1")
            return Result.Fail(ErrorCode.OperationFailed,
                $"object.register: no detection to register — '{from}_found' is not 1. Run a Detect Object block with capture prefix '{from}' first.");

        var xText = ctx.GetString($"{from}_x");
        var yText = ctx.GetString($"{from}_y");
        if (!double.TryParse(xText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(yText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
            return Result.Fail(ErrorCode.InvalidParameter, $"object.register: '{from}_x' / '{from}_y' are not numbers ('{xText}', '{yText}').");

        bool show = !Has(step, "show") || Bool(step, "show", ctx);

        // Optional outline snapshot: a box around the crosshair, filtered to black / white.
        GrippedOutlineOptions? outline = null;
        if (Bool(step, "outline", ctx))
        {
            int w = Has(step, "roiWidth") ? (int)Num(step, "roiWidth", ctx) : GrippedOutlineOptions.DefaultSize;
            int h = Has(step, "roiHeight") ? (int)Num(step, "roiHeight", ctx) : GrippedOutlineOptions.DefaultSize;
            outline = new GrippedOutlineOptions(w, h,
                GrippedOutlineOptions.ParseFilters(step.Parameters.GetValueOrDefault("filters", string.Empty)));
        }

        var r = await _registerGrippedMarker(Str(step, "name", ctx), tool, x, y, show, outline).ConfigureAwait(false);
        return r.IsSuccess ? Result.Ok() : Result.Fail(r.Error, r.Message);
    }

    private Result RemoveBeacon(WorkflowStep step, WorkflowContext ctx)
    {
        bool byName = Str(step, "which", ctx).Trim().Equals("name", StringComparison.OrdinalIgnoreCase);
        if (!byName)
        {
            int n = _beacons.Clear();
            _log(LogLevel.Info, n > 0 ? $"All beacons removed ({n})." : "Remove beacons: there were none.");
            return Result.Ok();
        }

        var name = Str(step, "name", ctx).Trim();
        if (name.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "beacon.remove: 'Remove' is set to 'name' but no beacon name is given.");
        // A beacon that is already gone (expired, replaced) is not a reason to stop a program.
        if (_beacons.Remove(name)) _log(LogLevel.Info, $"Beacon '{name}' removed.");
        else _log(LogLevel.Warning, $"Remove beacon: there is no beacon named '{name}'.");
        return Result.Ok();
    }
}
