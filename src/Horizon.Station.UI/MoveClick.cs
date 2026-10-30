using System;
using System.Threading.Tasks;
using System.Windows;
using Horizon.Engine;

namespace Horizon.Station.UI;

/// <summary>
/// Shared "MoveClick" maths used by the Run-page feed and the camera pop-out:
/// measure the real-world offset from a feed's crosshair centre to a click point,
/// and jog the configured device by that offset. Split into a synchronous
/// <see cref="Measure"/> (so callers can show the read-out instantly) and an async
/// <see cref="JogAsync"/> (which awaits the move).
/// </summary>
public static class MoveClick
{
    /// <summary>A measured offset from the crosshair centre, in millimetres.</summary>
    public readonly record struct Measurement(double DxMm, double DyMm, double DistMm)
    {
        public string Readout => $"dx {DxMm:+0.000;-0.000}  dy {DyMm:+0.000;-0.000}  d {DistMm:0.000} mm";
    }

    /// <summary>
    /// Converts a click at <paramref name="click"/> on a stretched feed
    /// (<paramref name="availW"/>×<paramref name="availH"/> display px showing a
    /// <paramref name="srcW"/>×<paramref name="srcH"/> source image) into a mm
    /// offset from the image centre. Returns null for a degenerate geometry.
    ///
    /// <paramref name="cover"/> selects the stretch the surface is drawn with, and it
    /// <b>must</b> match the XAML — <c>false</c> for <c>Stretch="Uniform"</c> (fit,
    /// letterboxed), <c>true</c> for <c>Stretch="UniformToFill"</c> (cover, cropped).
    /// Both centre the source on the surface, so only the scale differs; getting it
    /// wrong silently mis-scales every jog.
    /// </summary>
    public static Measurement? Measure(EngineHost host, Point click, double availW, double availH,
                                       int srcW, int srcH, bool cover = false)
    {
        if (availW <= 0 || availH <= 0 || srcW <= 0 || srcH <= 0) return null;
        double scale = DisplayScale(availW, availH, srcW, srcH, cover);   // source px → display px
        if (scale <= 0) return null;

        double mmPerPixel = host.Settings.Current.MmPerPixel;
        double dxMm = ((click.X - availW / 2.0) / scale) * mmPerPixel;
        double dyMm = ((click.Y - availH / 2.0) / scale) * mmPerPixel;
        return new Measurement(dxMm, dyMm, Math.Sqrt(dxMm * dxMm + dyMm * dyMm));
    }

    /// <summary>
    /// Source px → display px for a centred image: <c>Min</c> fits the whole frame
    /// inside the surface (<c>Uniform</c>), <c>Max</c> covers the surface and crops
    /// the overflow (<c>UniformToFill</c>). Shared by the click maths and by every
    /// overlay that has to line up with the drawn pixels (ROI box, detection line).
    /// </summary>
    public static double DisplayScale(double availW, double availH, int srcW, int srcH, bool cover)
    {
        if (availW <= 0 || availH <= 0 || srcW <= 0 || srcH <= 0) return 0;
        return cover
            ? Math.Max(availW / srcW, availH / srcH)
            : Math.Min(availW / srcW, availH / srcH);
    }

    /// <summary>
    /// Jogs <c>MoveClickDevice</c> by the measured offset — X and Y simultaneously
    /// via one coordinated absolute move (read current pose, add the per-axis
    /// offsets, move). No-op when no device is configured. Logs the outcome.
    /// </summary>
    public static async Task JogAsync(EngineHost host, Measurement m, Action<string> log)
    {
        var s = host.Settings.Current;
        if (string.IsNullOrWhiteSpace(s.MoveClickDevice)) return;

        double xJog = m.DxMm * s.MoveClickXSign;
        double yJog = m.DyMm * s.MoveClickYSign;
        try
        {
            var pos = await host.Engine.Motion.GetPositionAsync(s.MoveClickDevice).ConfigureAwait(true);
            if (!pos.IsSuccess)
            {
                log($"MoveClick jog — position read failed ({s.MoveClickDevice}): {pos.Message}");
                return;
            }

            var target = AddAxis(AddAxis(pos.Value, s.MoveClickXAxis, xJog), s.MoveClickYAxis, yJog);
            var move = await host.Engine.Motion.MoveToPositionAsync(s.MoveClickDevice, target).ConfigureAwait(true);
            log(move.IsSuccess
                ? $"MoveClick jog {s.MoveClickDevice}: {s.MoveClickXAxis} {xJog:+0.000;-0.000}, {s.MoveClickYAxis} {yJog:+0.000;-0.000} mm (simultaneous)"
                : $"MoveClick jog failed ({s.MoveClickDevice}): {move.Message}");
        }
        catch (Exception ex)
        {
            log($"MoveClick jog error: {ex.Message}");
        }
    }

    /// <summary>Returns a copy of <paramref name="p"/> with <paramref name="delta"/> added to the named axis.</summary>
    public static MotionPosition AddAxis(MotionPosition p, string axis, double delta) => axis?.Trim().ToUpperInvariant() switch
    {
        "X" => p with { X = p.X + delta },
        "Y" => p with { Y = p.Y + delta },
        "Z" => p with { Z = p.Z + delta },
        "U" => p with { U = p.U + delta },
        "V" => p with { V = p.V + delta },
        "W" => p with { W = p.W + delta },
        _ => p,
    };
}
