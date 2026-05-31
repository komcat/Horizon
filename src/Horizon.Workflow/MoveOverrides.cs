using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Optional per-step settings applied at the <b>start</b> of a move command:
/// a velocity (mm/s; 0 = leave current), and a camera + exposure (µs) to apply
/// while the stage is already travelling. Empty fields leave the corresponding
/// hardware untouched (for a named-position move, they fall back to the position's
/// stored settings).
/// </summary>
public sealed record MoveOverrides(double Velocity = 0, string? Camera = null, double? ExposureUs = null);

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
