using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Horizon.Core.Dispensing;

public sealed class DispenseProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string ProductsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "products");

    public DispenseProfile? Load(string productName, string profileName)
    {
        var path = GetPath(productName, profileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DispenseProfile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Dispense] Failed to load profile {Name}", profileName);
            return null;
        }
    }

    public void Save(string productName, DispenseProfile profile)
    {
        var dir = GetDir(productName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{profile.Name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        Log.Information("[Dispense] Profile saved: {Name}", profile.Name);
    }

    public List<string> GetAllProfileNames(string productName)
    {
        var dir = GetDir(productName);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetDir(string productName)
        => Path.Combine(ProductsRoot, productName, "dispense");

    private static string GetPath(string productName, string profileName)
        => Path.Combine(GetDir(productName), $"{profileName}.json");
}
