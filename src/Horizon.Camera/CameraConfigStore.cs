using System.Text.Json;
using Horizon.Camera.Models;
using Serilog;

namespace Horizon.Camera;

public sealed class CameraConfigStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "config", "camera-config.json");

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
            {
                Log.Warning("[Camera] Config file not found: {FilePath}", FilePath);
                return new List<RegisteredCamera>();
            }

            var json = File.ReadAllText(FilePath);
            var result = JsonSerializer.Deserialize<List<RegisteredCamera>>(json, JsonOptions)
                         ?? new List<RegisteredCamera>();
            Log.Information("[Camera] Config file loaded: {FilePath}, {Count} camera(s)", FilePath, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("[Camera] Failed to load config file: {FilePath} — {Message}", FilePath, ex.Message);
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
            Log.Information("[Camera] Config saved: {FilePath}, {Count} camera(s)", FilePath, cameras.Count);
        }
        catch (Exception ex)
        {
            Log.Error("[Camera] Failed to save config file: {FilePath} — {Message}", FilePath, ex.Message);
        }
    }
}
