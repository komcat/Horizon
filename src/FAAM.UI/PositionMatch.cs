using Horizon.Engine;

namespace FAAM.UI;

/// <summary>
/// "Is the device standing on this taught point?" — shared by the Run-page motion
/// inspector and the Machine › Motion position map so both agree on what "at a
/// position" means.
/// </summary>
public static class PositionMatch
{
    /// <summary>A device counts as "standing on" a taught point within this much on every axis (mm / deg).</summary>
    public const double Tolerance = 0.01;

    /// <summary>True when the live pose sits within <see cref="Tolerance"/> of the taught point on every axis the device owns.</summary>
    public static bool Matches(MotionPosition? taught, MotionPosition live, IReadOnlyList<string> axes)
    {
        if (taught == null) return false;
        foreach (var axis in axes)
        {
            double delta = axis.Trim().ToUpperInvariant() switch
            {
                "X" => live.X - taught.X,
                "Y" => live.Y - taught.Y,
                "Z" => live.Z - taught.Z,
                "U" => live.U - taught.U,
                "V" => live.V - taught.V,
                "W" => live.W - taught.W,
                _ => 0,
            };
            if (Math.Abs(delta) > Tolerance) return false;
        }
        return true;
    }
}
