using System.IO;
using System.Text.Json;

namespace Horizon.Core;

public sealed class MoveMapStore
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

    public MoveMapData Load(string productName)
    {
        if (string.IsNullOrEmpty(productName))
            return new MoveMapData();

        var path = GetPath(productName);
        if (!File.Exists(path))
            return new MoveMapData();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MoveMapData>(json, JsonOptions) ?? new MoveMapData();
        }
        catch
        {
            return new MoveMapData();
        }
    }

    public void Save(string productName, MoveMapData data)
    {
        if (string.IsNullOrEmpty(productName)) return;

        var dir = Path.Combine(ProductsRoot, productName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "movemap.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private static string GetPath(string productName)
        => Path.Combine(ProductsRoot, productName, "movemap.json");
}
