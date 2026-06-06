using System.Text.Json;

namespace Horizon.Workflow;

/// <summary>
/// Identity / metadata for a product, persisted as <c>product.json</c> in the
/// product directory. The product's canonical name is its <b>directory name</b>
/// (<see cref="Name"/>); the rest is descriptive. A product whose folder predates
/// this file reads back with defaults and an empty <see cref="DisplayName"/>.
/// </summary>
public sealed record ProductInfo
{
    /// <summary>Canonical name = the product directory name. Authoritative.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Operator-facing name; falls back to <see cref="Name"/> when empty.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Part / device number this product builds. Free-form.</summary>
    public string PartNumber { get; init; } = string.Empty;

    /// <summary>Free-form description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>On-disk schema version, for future migrations.</summary>
    public int SchemaVersion { get; init; } = 1;

    public DateTime CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }

    /// <summary>The name to show — <see cref="DisplayName"/> if set, else <see cref="Name"/>.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>
/// Product directories under <c>%APPDATA%\Horizon\products\</c> — the shared
/// product root (same as the legacy app). A product is a directory holding
/// <c>processes\</c>, <c>recipes\</c>, and <c>decks\</c> plus a <c>product.json</c>
/// metadata file; the active product is recorded in <c>active.json</c>. The layout
/// is compatible with the legacy app — only one app should run at a time.
/// </summary>
internal sealed class ProductStore
{
    private const string InfoFile = "product.json";

    // camelCase + case-insensitive: active.json is shared with the legacy app,
    // which writes camelCase — read it leniently, write it the same way.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;
    private readonly string _activePath;

    public ProductStore()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "products");
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
        Directory.CreateDirectory(Path.Combine(dir, "decks"));

        var now = DateTime.UtcNow;
        WriteInfo(new ProductInfo { Name = name, DisplayName = name, CreatedUtc = now, ModifiedUtc = now });
    }

    /// <summary>
    /// Creates <paramref name="newName"/> as a full editable copy of
    /// <paramref name="source"/>'s process definition — <c>positions.json</c>, the
    /// <c>vision\</c> profiles, and the <c>processes\</c> / <c>decks\</c> /
    /// <c>recipes\</c> programs — so a model variation starts from a working process
    /// the operator can re-teach positions on and edit command blocks in. Only
    /// <c>dut\</c> run history is NOT copied (it belongs to the source's physical
    /// units). The clone gets a fresh identity + timestamps, seeded with the source's
    /// part number / description. Callers must have validated that
    /// <paramref name="source"/> exists and <paramref name="newName"/> is valid and free.
    /// </summary>
    public void Clone(string source, string newName)
    {
        var src = Path.Combine(_root, source);
        var dst = Path.Combine(_root, newName);
        Create(newName);   // dirs + a default product.json

        CopyFile(Path.Combine(src, "positions.json"), Path.Combine(dst, "positions.json"));
        foreach (var sub in new[] { "vision", "processes", "decks", "recipes" })
            CopyDirectory(Path.Combine(src, sub), Path.Combine(dst, sub));

        var s = ReadInfo(source);
        var now = DateTime.UtcNow;
        WriteInfo(new ProductInfo
        {
            Name = newName,
            DisplayName = newName,
            PartNumber = s?.PartNumber ?? string.Empty,
            Description = s?.Description ?? string.Empty,
            CreatedUtc = now,
            ModifiedUtc = now,
        });
    }

    private static void CopyFile(string src, string dst)
    {
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }

    private static void CopyDirectory(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Reads a product's <c>product.json</c>. Returns a defaulted record (with the
    /// directory name) when the file is absent or unreadable — so products created
    /// before metadata existed still resolve. Returns null only if the product
    /// directory itself does not exist.
    /// </summary>
    public ProductInfo? ReadInfo(string name)
    {
        if (!Exists(name)) return null;

        var path = Path.Combine(_root, name, InfoFile);
        if (File.Exists(path))
        {
            try
            {
                var info = JsonSerializer.Deserialize<ProductInfo>(File.ReadAllText(path), JsonOptions);
                if (info != null)
                    return info with { Name = name };   // directory name is authoritative
            }
            catch
            {
                // Unreadable — fall through to a defaulted record.
            }
        }
        return new ProductInfo { Name = name, DisplayName = name };
    }

    /// <summary>Writes <c>product.json</c> for a product, refreshing <c>ModifiedUtc</c>.</summary>
    public void WriteInfo(ProductInfo info)
    {
        var dir = Path.Combine(_root, info.Name);
        Directory.CreateDirectory(dir);
        var stamped = info with { ModifiedUtc = DateTime.UtcNow };
        File.WriteAllText(Path.Combine(dir, InfoFile), JsonSerializer.Serialize(stamped, JsonOptions));
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
                var doc = JsonSerializer.Deserialize<ActiveDoc>(File.ReadAllText(_activePath), JsonOptions);
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
