using Horizon.Common;

namespace Horizon.Core;

public record TransformEntry
{
    public string DeviceId { get; init; } = "";
    public TransformMatrix Matrix { get; init; } = new();
}

public record TransformMatrix
{
    public double M11 { get; init; }
    public double M12 { get; init; }
    public double M13 { get; init; }
    public double M21 { get; init; }
    public double M22 { get; init; }
    public double M23 { get; init; }
    public double M31 { get; init; }
    public double M32 { get; init; }
    public double M33 { get; init; }
}

public class AxisTransformService
{
    private readonly Dictionary<string, TransformMatrix> _matrices = new(StringComparer.OrdinalIgnoreCase);

    public int LoadConfig(string configDir)
    {
        var entries = ConfigLoader.LoadJson<List<TransformEntry>>(configDir, "axis_transforms");
        if (entries == null) return 0;
        foreach (var e in entries)
            _matrices[e.DeviceId] = e.Matrix;
        return entries.Count;
    }

    public (double X, double Y, double Z) GlobalToLocal(string deviceId, double gX, double gY, double gZ)
    {
        if (!_matrices.TryGetValue(deviceId, out var m))
            return (gX, gY, gZ);
        return (
            m.M11 * gX + m.M12 * gY + m.M13 * gZ,
            m.M21 * gX + m.M22 * gY + m.M23 * gZ,
            m.M31 * gX + m.M32 * gY + m.M33 * gZ
        );
    }

    public (double X, double Y, double Z) LocalToGlobal(string deviceId, double lX, double lY, double lZ)
    {
        if (!_matrices.TryGetValue(deviceId, out var m))
            return (lX, lY, lZ);
        return (
            m.M11 * lX + m.M21 * lY + m.M31 * lZ,
            m.M12 * lX + m.M22 * lY + m.M32 * lZ,
            m.M13 * lX + m.M23 * lY + m.M33 * lZ
        );
    }

    public bool HasTransform(string deviceId) => _matrices.ContainsKey(deviceId);
}
