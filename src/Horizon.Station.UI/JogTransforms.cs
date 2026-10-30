using Horizon.Common;

namespace Horizon.Station.UI;

/// <summary>A 3×3 jog transform — maps a global jog vector into a device-local frame.</summary>
public sealed record JogMatrix
{
    public double M11 { get; init; } = 1; public double M12 { get; init; } public double M13 { get; init; }
    public double M21 { get; init; } public double M22 { get; init; } = 1; public double M23 { get; init; }
    public double M31 { get; init; } public double M32 { get; init; } public double M33 { get; init; } = 1;

    public static readonly JogMatrix Identity = new();

    /// <summary>Transforms a global vector into the device-local frame (local = M · global).</summary>
    public (double X, double Y, double Z) Apply(double gx, double gy, double gz) => (
        M11 * gx + M12 * gy + M13 * gz,
        M21 * gx + M22 * gy + M23 * gz,
        M31 * gx + M32 * gy + M33 * gz);
}

/// <summary>One device's jog transform entry (matches jog_transforms.json).</summary>
public sealed record JogDeviceTransform
{
    public string DeviceId { get; init; } = "";
    public JogMatrix Matrix { get; init; } = new();
}

/// <summary>
/// Per-device jog transforms (global jog axis → device-local axis). A Layer 3 /
/// application concern — loaded from <c>jog_transforms.json</c> in the config dir,
/// seeded with defaults on first run. Identity is used for any device not listed.
/// </summary>
public sealed class JogTransformStore
{
    private const string FileName = "jog_transforms";
    private readonly Dictionary<string, JogMatrix> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    public JogTransformStore()
    {
        var dir = ConfigLoader.GetConfigDirectory();
        var list = ConfigLoader.LoadJson<List<JogDeviceTransform>>(dir, FileName);
        if (list == null || list.Count == 0)
        {
            list = Defaults();
            ConfigLoader.SaveJson(dir, FileName, list);   // seed the file so it's editable
        }
        foreach (var t in list)
            if (!string.IsNullOrWhiteSpace(t.DeviceId))
                _byDevice[t.DeviceId] = t.Matrix;
    }

    /// <summary>The transform for a device, or identity if it has none configured.</summary>
    public JogMatrix Get(string device)
        => _byDevice.TryGetValue(device, out var m) ? m : JogMatrix.Identity;

    private static List<JogDeviceTransform> Defaults() => new()
    {
        new() { DeviceId = "hex-left",    Matrix = new() { M11 = 0, M12 = 0,  M13 = 1, M21 = 0, M22 = 1,  M23 = 0, M31 = 1,  M32 = 0, M33 = 0 } },
        new() { DeviceId = "hex-bottom",  Matrix = new() { M11 = 0, M12 = -1, M13 = 0, M21 = 1, M22 = 0,  M23 = 0, M31 = 0,  M32 = 0, M33 = 1 } },
        new() { DeviceId = "hex-right",   Matrix = new() { M11 = 0, M12 = 0,  M13 = 1, M21 = 0, M22 = -1, M23 = 0, M31 = -1, M32 = 0, M33 = 0 } },
        new() { DeviceId = "gantry-main", Matrix = new() { M11 = 1, M12 = 0,  M13 = 0, M21 = 0, M22 = 1,  M23 = 0, M31 = 0,  M32 = 0, M33 = -1 } },
        new() { DeviceId = "stage-main",  Matrix = new() { M11 = 1, M12 = 0,  M13 = 0, M21 = 0, M22 = 1,  M23 = 0, M31 = 0,  M32 = 0, M33 = 1 } },
        new() { DeviceId = "xy-table",    Matrix = new() { M11 = 1, M12 = 0,  M13 = 0, M21 = 0, M22 = 1,  M23 = 0, M31 = 0,  M32 = 0, M33 = 1 } },
    };
}
