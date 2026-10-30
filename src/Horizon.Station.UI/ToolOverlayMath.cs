using System;
using System.Windows;
using Horizon.Common;

namespace Horizon.Station.UI;

/// <summary>
/// Shared conversions for the device-registration overlay and the registration picker:
/// camera-device-frame millimetres ↔ camera source pixels ↔ display pixels. Uses the same
/// mm/pixel + MoveClick axis signs as <see cref="MoveClick"/>, so "the tool is here on the
/// image" and "click here to jog onto it" agree by construction.
/// </summary>
public static class ToolOverlayMath
{
    /// <summary>The ±1 sign for an axis, treating 0 / unset as +1.</summary>
    private static double Sign(double s) => s < 0 ? -1.0 : 1.0;

    /// <summary>
    /// Source-pixel offset from the image centre → camera-device-frame offset (mm): the relative
    /// camera-device move that would centre the camera on that pixel (MoveClick semantics).
    /// </summary>
    public static (double DxMm, double DyMm) SourcePxToMm(double dxPx, double dyPx, AppSettings s)
        => (dxPx * s.MmPerPixel * Sign(s.MoveClickXSign), dyPx * s.MmPerPixel * Sign(s.MoveClickYSign));

    /// <summary>Camera-device-frame offset (mm, tool − camera) → source-pixel offset from the image centre.</summary>
    public static (double DxPx, double DyPx)? MmToSourcePx(double dxMm, double dyMm, AppSettings s)
    {
        if (s.MmPerPixel <= 0) return null;
        return (dxMm / s.MmPerPixel * Sign(s.MoveClickXSign), dyMm / s.MmPerPixel * Sign(s.MoveClickYSign));
    }

    /// <summary>Source-pixel offset from centre → a point on a display box of <paramref name="availW"/>×<paramref name="availH"/> at <paramref name="scale"/>.</summary>
    public static Point ToDisplay(double dxPx, double dyPx, double availW, double availH, double scale)
        => new(availW / 2.0 + dxPx * scale, availH / 2.0 + dyPx * scale);

    /// <summary>Display point → source-pixel offset from the image centre (inverse of <see cref="ToDisplay"/>).</summary>
    public static (double DxPx, double DyPx) FromDisplay(Point p, double availW, double availH, double scale)
        => ((p.X - availW / 2.0) / scale, (p.Y - availH / 2.0) / scale);

    /// <summary>
    /// Clamps <paramref name="p"/> into the box [inset, w−inset]×[inset, h−inset], axis by
    /// axis, so an off-screen point lands on the edge nearest to where it really is.
    /// <paramref name="clamped"/> reports whether it had to move.
    /// </summary>
    public static Point ClampToBox(Point p, double w, double h, double inset, out bool clamped)
    {
        double x = Math.Clamp(p.X, inset, Math.Max(inset, w - inset));
        double y = Math.Clamp(p.Y, inset, Math.Max(inset, h - inset));
        clamped = Math.Abs(x - p.X) > 1e-6 || Math.Abs(y - p.Y) > 1e-6;
        return new Point(x, y);
    }
}
