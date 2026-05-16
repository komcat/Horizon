using System.Text.Json;

namespace Horizon.Workflow;

/// <summary>
/// Layer 2 product directories under <c>%APPDATA%\Horizon\workflow\products\</c>
/// — a root distinct from the legacy app's products folder, so the two never
/// collide. A product is a directory holding <c>processes\</c> and
/// <c>recipes\</c>; the active product is recorded in <c>active.json</c>.
/// </summary>
internal sealed class ProductStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _root;
    private readonly string _activePath;

    public ProductStore()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "workflow", "products");
        _activePath = Path.Combine(_root, "active.json");
        Directory.CreateDirectory(_root);
    }

    /// <summary>Absolute path of the Layer 2 product root.</summary>
    public string Root => _root;

    /// <summary>Absolute directory of a product (whether or not it exists).</summary>
    public string ProductDir(string name) => Path.Combine(_root, name);

    /// <summary>Absolute directory of the active product, or null if none.</summary>
    public string? ActiveProductDir
    {
        get
        {
            var active = ActiveProduct;
            return active == null ? null : Path.Combine(_root, active);
        }
    }

    public IReadOnlyList<string> GetProductNames()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        return Directory.GetDirectories(_root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool Exists(string name)
        => Directory.Exists(Path.Combine(_root, name));

    public bool IsValidName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public void Create(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "processes"));
        Directory.CreateDirectory(Path.Combine(dir, "recipes"));
    }

    public void Delete(string name)
    {
        var dir = Path.Combine(_root, name);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    public string? ActiveProduct
    {
        get
        {
            if (!File.Exists(_activePath)) return null;
            try
            {
                var doc = JsonSerializer.Deserialize<ActiveDoc>(File.ReadAllText(_activePath));
                return string.IsNullOrEmpty(doc?.ActiveProduct) ? null : doc!.ActiveProduct;
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetActive(string? name)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_activePath,
            JsonSerializer.Serialize(new ActiveDoc { ActiveProduct = name ?? string.Empty }, JsonOptions));
    }

    private sealed record ActiveDoc
    {
        public string ActiveProduct { get; init; } = string.Empty;
    }
}
