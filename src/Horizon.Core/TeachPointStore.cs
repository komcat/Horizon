using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Core;

public record TeachPoint
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    [JsonPropertyName("positionName")]
    public string PositionName { get; init; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }

    [JsonPropertyName("u")]
    public double U { get; init; }

    [JsonPropertyName("v")]
    public double V { get; init; }

    [JsonPropertyName("w")]
    public double W { get; init; }
}

public record TeachPointProduct
{
    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("points")]
    public List<TeachPoint> Points { get; init; } = new();
}

public record TeachPointActiveConfig
{
    [JsonPropertyName("activeProduct")]
    public string ActiveProduct { get; init; } = string.Empty;
}

public sealed class TeachPointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _directory;

    public TeachPointStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "teach-points");
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
    }

    public List<string> GetProductNames()
    {
        if (!Directory.Exists(_directory))
            return new();

        return Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && !n.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TeachPointProduct? LoadProduct(string productName)
    {
        var path = Path.Combine(_directory, $"{productName}.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TeachPointProduct>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveProduct(TeachPointProduct product)
    {
        EnsureDirectory();
        var path = Path.Combine(_directory, $"{product.ProductName}.json");
        var json = JsonSerializer.Serialize(product, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void DeleteProduct(string productName)
    {
        var path = Path.Combine(_directory, $"{productName}.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    public void RenameProduct(string oldName, string newName)
    {
        var oldPath = Path.Combine(_directory, $"{oldName}.json");
        var newPath = Path.Combine(_directory, $"{newName}.json");
        if (!File.Exists(oldPath)) return;

        var product = LoadProduct(oldName);
        if (product == null) return;

        var renamed = product with { ProductName = newName };
        SaveProduct(renamed);
        File.Delete(oldPath);
    }

    public string GetActiveProductName()
    {
        var path = Path.Combine(_directory, "active.json");
        if (!File.Exists(path))
            return string.Empty;

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<TeachPointActiveConfig>(json, JsonOptions);
            return config?.ActiveProduct ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SetActiveProductName(string productName)
    {
        EnsureDirectory();
        var path = Path.Combine(_directory, "active.json");
        var config = new TeachPointActiveConfig { ActiveProduct = productName };
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }
}
