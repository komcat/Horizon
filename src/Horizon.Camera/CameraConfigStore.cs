using System.Text.Json;
using Horizon.Camera.Models;

namespace Horizon.Camera;

public sealed class CameraConfigStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "camera-config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public List<RegisteredCamera> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<RegisteredCamera>();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<RegisteredCamera>>(json, JsonOptions)
                   ?? new List<RegisteredCamera>();
        }
        catch
        {
            return new List<RegisteredCamera>();
        }
    }

    public void Save(List<RegisteredCamera> cameras)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(cameras, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Swallow — caller can retry or log
        }
    }
}
