using System.IO;
using System.Text.Json;

namespace Horizon.Core;

public sealed class NeedleCameraCalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string ProductsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "products");

    public NeedleCameraCalibration? Load(string productName)
    {
        if (string.IsNullOrEmpty(productName))
            return null;

        var path = GetPath(productName);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NeedleCameraCalibration>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string productName, NeedleCameraCalibration calibration)
    {
        if (string.IsNullOrEmpty(productName)) return;

        var dir = Path.Combine(ProductsRoot, productName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "needle-camera-cal.json");
        File.WriteAllText(path, JsonSerializer.Serialize(calibration, JsonOptions));
    }

    private static string GetPath(string productName)
        => Path.Combine(ProductsRoot, productName, "needle-camera-cal.json");
}
