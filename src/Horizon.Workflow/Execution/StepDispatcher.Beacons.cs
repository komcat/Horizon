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
