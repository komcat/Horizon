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
    /// Gets the standard config directory path: %APPDATA%\Horizon\config\
    /// </summary>
    public static string GetConfigDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "config");
    }
}
