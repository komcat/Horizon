using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Optional per-step settings applied at the <b>start</b> of a move command:
/// a velocity (mm/s; 0 = leave current), and a camera + exposure (µs) to apply
/// while the stage is already travelling. Empty fields leave the corresponding
/// hardware untouched (for a named-position move, they fall back to the position's
/// stored settings). A named-position move may also slow down for the last
/// <see cref="ApproachDistance"/> mm to <see cref="ApproachVelocity"/> mm/s, then restore its
/// velocity on arrival — both must be &gt; 0 to apply (see <see cref="MoveApproach"/>).
/// </summary>
public sealed record MoveOverrides(
    double Velocity = 0, string? Camera = null, double? ExposureUs = null, MoveLighting? Lighting = null,
    double ApproachDistance = 0, double ApproachVelocity = 0);

/// <summary>
/// A lighting call-out applied at the start of a move (null on <see cref="MoveOverrides"/>
/// = leave lighting untouched). <see cref="Light"/> is a light name (channel group or
/// ungrouped channel); the controller that owns it is found automatically.
/// </summary>
public sealed record MoveLighting(string Light, LightColor Color, double Percent);

/// <summary>Applies a <see cref="MoveLighting"/> while a move is in flight.</summary>
internal static class MoveLight
{
    /// <summary>
    /// Resolves the controller owning <c>lighting.Light</c> and sets the call-out.
    /// Returns the failure instead of throwing, so the caller can let the move settle
    /// first and then fail the step (a vision step after it would otherwise run in the
    /// wrong light).
    /// </summary>
    public static async Task<Result> ApplyAsync(
        ILightingFacet facet, MoveLighting? lighting, Action<LogLevel, string> log)
    {
        if (lighting == null) return Result.Ok();

        var owners = facet.DeviceNames
            .Where(d => facet.GetDevice(d)?.Lights.Contains(lighting.Light, StringComparer.OrdinalIgnoreCase) == true)
            .ToList();
        if (owners.Count != 1)
            return Result.Fail(ErrorCode.InvalidParameter, owners.Count == 0
                ? $"Light '{lighting.Light}' is not configured on any lighting controller."
                : $"Light '{lighting.Light}' exists on several controllers ({string.Join(", ", owners)}).");

        var result = await facet.SetLightColorAsync(owners[0], lighting.Light, lighting.Color, lighting.Percent)
                                .ConfigureAwait(false);
        log(result.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            $"Light {lighting.Light} = {lighting.Color} {lighting.Percent:0.#} %: {(result.IsSuccess ? "ok" : result.Message)}");
        return result;
    }
}

/// <summary>Shared "apply a manual exposure during a move" helper for the move ops.</summary>
internal static class MoveExposure
{
    /// <summary>
    /// Applies a fixed exposure to a camera with auto-exposure turned off — meant to
    /// be awaited <b>while a motion device is already moving</b>, so the camera's
    /// ~300 ms settle overlaps the move rather than adding to it. Never throws; a
    /// camera failure is logged and does not fail the move.
    /// </summary>
    public static async Task ApplyAsync(
        ICameraFacet camera, string? cameraId, double? exposureUs, Action<LogLevel, string> log)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || exposureUs is not > 0) return;
        try
        {
            // A pinned exposure means "use exactly this", so auto-exposure must be off
            // or the adapter keeps auto-adjusting and ignores the manual value.
            var current = camera.GetState(cameraId).Exposure;
            var applied = await camera
                .ApplyExposureAsync(cameraId, current with { ExposureUs = exposureUs.Value, AutoExposure = false })
                .ConfigureAwait(false);
            log(applied.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                $"Exposure {cameraId} = {exposureUs}µs: {(applied.IsSuccess ? "ok" : applied.Message)}");
        }
        catch (Exception ex)
        {
            log(LogLevel.Warning, $"Exposure {cameraId} = {exposureUs}µs: {ex.Message}");
        }
    }
}
