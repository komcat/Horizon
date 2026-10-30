namespace Horizon.Workflow.Scanning;

/// <summary>NaN-safe range checks for scan parameter validation — a NaN fails every check.</summary>
internal static class ScanLimits
{
    /// <summary>True when <paramref name="v"/> is in (<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static bool InRange(double v, double min, double max) => v > min && v <= max;

    /// <summary>True when <paramref name="v"/> is a positive number.</summary>
    public static bool Positive(double v) => v > 0;
}
