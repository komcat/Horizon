using System.Text.Json;

namespace Horizon.ConfigTool;

/// <summary>
/// ConfigTool-local mirror of <c>Horizon.Camera.Models.CameraType</c>. Duplicated
/// (not referenced) so this authoring tool stays free of the camera SDK native
/// dependencies (Basler Pylon / IDS). Integer values MUST match the real enum
/// (Pylon=0, IDS=1, Unknown=2) — <c>camera-config.json</c> stores SdkType as an integer.
/// </summary>
public enum CameraSdkType
{
    Pylon,
    IDS,
    Unknown,
}

/// <summary>
/// ConfigTool-local mirror of <c>Horizon.Camera.Models.RegisteredCamera</c> — the
/// entry shape of <c>camera-config.json</c>. Kept in sync by hand; that type is
/// the source of truth.
/// </summary>
public record CameraConfigEntry
{
    public string Name { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public CameraSdkType SdkType { get; init; } = CameraSdkType.Pylon;
    /// <summary>"USB" or "IP".</summary>
    public string ConnectionType { get; init; } = "USB";
    public string IpAddress { get; init; } = string.Empty;
    /// <summary>0 = auto-detect from hardware.</summary>
    public int Width { get; init; }
    /// <summary>0 = auto-detect from hardware.</summary>
    public int Height { get; init; }
    /// <summary>0 = uncalibrated.</summary>
    public double MmPerPixel { get; init; }
    public double Magnification { get; init; } = 1.0;
}

/// <summary>
/// Reads / writes <c>camera-config.json</c> — a plain JSON array of
/// <see cref="CameraConfigEntry"/> — in the same on-disk format the engine's
/// CameraConfigStore expects: PascalCase keys, enum-as-integer, no wrapper object.
/// Unlike the other domains this does NOT go through <c>ConfigLoader</c> (which
/// would write camelCase + enum-as-string and break the engine reader).
/// </summary>
internal static class CameraConfigJson
{
    private const string FileName = "camera-config.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static List<CameraConfigEntry> Load(string configDir)
    {
        var path = Path.Combine(configDir, FileName);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<CameraConfigEntry>>(File.ReadAllText(path), Options) ?? new();
    }

    public static void Save(IReadOnlyList<CameraConfigEntry> cameras, string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(cameras.ToList(), Options));
    }
}
