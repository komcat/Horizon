using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Common;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Loads a JSON config file with local/default fallback.
    /// Tries {name}.local.json first, then {name}.json.
    /// </summary>
    public static T? LoadJson<T>(string basePath, string name)
    {
        var localPath = Path.Combine(basePath, $"{name}.local.json");
        var defaultPath = Path.Combine(basePath, $"{name}.json");

        var path = File.Exists(localPath) ? localPath : defaultPath;

        if (!File.Exists(path))
            return default;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>
    /// Writes a JSON config file as <c>{name}.json</c> (indented, camelCase).
    /// Never writes <c>.local.json</c> — a hand-authored local override is left
    /// untouched. The directory is created if missing.
    /// </summary>
    public static void SaveJson<T>(string basePath, string name, T value)
    {
        Directory.CreateDirectory(basePath);
        var path = Path.Combine(basePath, $"{name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions));
    }

    /// <summary>
    /// Gets the standard config directory path: %APPDATA%\Horizon\config\
    /// </summary>
    public static string GetConfigDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "config");
    }
}
