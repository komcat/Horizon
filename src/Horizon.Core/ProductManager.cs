using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Core;

public record ActiveProductConfig
{
    [JsonPropertyName("activeProduct")]
    public string ActiveProduct { get; init; } = string.Empty;
}

public sealed class ProductManager
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

    private static readonly string SharedRecipeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "recipes");

    private static readonly string SharedProcessDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "processes");

    private static readonly string SharedDeckDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "decks");

    private static readonly string ActiveConfigPath = Path.Combine(ProductsRoot, "active.json");

    private readonly TeachPointManager _teachPointManager;
    private readonly ILogSink? _logSink;

    private string _activeProductName = string.Empty;

    public string ActiveProductName => _activeProductName;

    public string? ActiveProductDir =>
        string.IsNullOrEmpty(_activeProductName)
            ? null
            : Path.Combine(ProductsRoot, _activeProductName);

    public event Action? ActiveProductChanged;

    public ProductManager(TeachPointManager teachPointManager, ILogSink? logSink = null)
    {
        _teachPointManager = teachPointManager;
        _logSink = logSink;
        Directory.CreateDirectory(ProductsRoot);
    }

    public void Load()
    {
        _activeProductName = ReadActiveProductName();

        // Migrate old teach-points if products folder is empty
        MigrateOldTeachPoints();
    }

    public List<string> GetProductNames()
    {
        if (!Directory.Exists(ProductsRoot))
            return new();

        return Directory.GetDirectories(ProductsRoot)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CreateProduct(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var productDir = Path.Combine(ProductsRoot, name);
        if (Directory.Exists(productDir)) return;

        Directory.CreateDirectory(productDir);
        Directory.CreateDirectory(Path.Combine(productDir, "recipes"));
        Directory.CreateDirectory(Path.Combine(productDir, "processes"));
        Directory.CreateDirectory(Path.Combine(productDir, "decks"));

        // Create empty positions file
        var posPath = Path.Combine(productDir, "positions.json");
        var emptyProduct = new TeachPointProduct { ProductName = name };
        File.WriteAllText(posPath, JsonSerializer.Serialize(emptyProduct, JsonOptions));

        _logSink?.Info($"Product created: {name}");
    }

    public void RenameProduct(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;

        var oldDir = Path.Combine(ProductsRoot, oldName);
        var newDir = Path.Combine(ProductsRoot, newName);
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

        Directory.Move(oldDir, newDir);

        // Update positions.json productName
        var posPath = Path.Combine(newDir, "positions.json");
        if (File.Exists(posPath))
        {
            try
            {
                var json = File.ReadAllText(posPath);
                var product = JsonSerializer.Deserialize<TeachPointProduct>(json, JsonOptions);
                if (product != null)
                {
                    var renamed = product with { ProductName = newName };
                    File.WriteAllText(posPath, JsonSerializer.Serialize(renamed, JsonOptions));
                }
            }
            catch { }
        }

        // Also rename in TeachPointManager
        _teachPointManager.RenameProduct(oldName, newName);

        if (_activeProductName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            _activeProductName = newName;
            WriteActiveProductName(newName);
            ActiveProductChanged?.Invoke();
        }

        _logSink?.Info($"Product renamed: {oldName} -> {newName}");
    }

    public void DeleteProduct(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var productDir = Path.Combine(ProductsRoot, name);
        if (!Directory.Exists(productDir)) return;

        try
        {
            Directory.Delete(productDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logSink?.Error($"Failed to delete product folder: {ex.Message}");
            return;
        }

        _teachPointManager.DeleteProduct(name);

        if (_activeProductName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            _activeProductName = string.Empty;
            WriteActiveProductName(string.Empty);
            ActiveProductChanged?.Invoke();
        }

        _logSink?.Info($"Product deleted: {name}");
    }

    public void LoadProduct(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var productDir = Path.Combine(ProductsRoot, name);
        if (!Directory.Exists(productDir)) return;

        _activeProductName = name;
        WriteActiveProductName(name);

        // Load positions from product folder into TeachPointManager
        var posPath = Path.Combine(productDir, "positions.json");
        if (File.Exists(posPath))
        {
            try
            {
                var json = File.ReadAllText(posPath);
                var product = JsonSerializer.Deserialize<TeachPointProduct>(json, JsonOptions);
                if (product != null)
                {
                    // Ensure product exists in TeachPointManager, then set active
                    if (_teachPointManager.GetProduct(name) == null)
                        _teachPointManager.CreateProduct(name);

                    // Sync points from product file into TeachPointManager
                    SyncPositionsToTeachPointManager(name, product);
                    _teachPointManager.SetActiveProduct(name);
                }
            }
            catch (Exception ex)
            {
                _logSink?.Error($"Failed to load product positions: {ex.Message}");
            }
        }
        else
        {
            // No positions file — just ensure product exists and activate it
            if (_teachPointManager.GetProduct(name) == null)
                _teachPointManager.CreateProduct(name);
            _teachPointManager.SetActiveProduct(name);
        }

        ActiveProductChanged?.Invoke();
        _logSink?.Info($"Product loaded: {name}");
    }

    public void UnloadProduct()
    {
        _activeProductName = string.Empty;
        WriteActiveProductName(string.Empty);
        _teachPointManager.SetActiveProduct(string.Empty);
        ActiveProductChanged?.Invoke();
        _logSink?.Info("Product unloaded");
    }

    public string GetRecipeDir()
    {
        if (!string.IsNullOrEmpty(_activeProductName))
        {
            var dir = Path.Combine(ProductsRoot, _activeProductName, "recipes");
            Directory.CreateDirectory(dir);
            return dir;
        }
        Directory.CreateDirectory(SharedRecipeDir);
        return SharedRecipeDir;
    }

    public string GetProcessDir()
    {
        if (!string.IsNullOrEmpty(_activeProductName))
        {
            var dir = Path.Combine(ProductsRoot, _activeProductName, "processes");
            Directory.CreateDirectory(dir);
            return dir;
        }
        Directory.CreateDirectory(SharedProcessDir);
        return SharedProcessDir;
    }

    public string GetDeckDir()
    {
        if (!string.IsNullOrEmpty(_activeProductName))
        {
            var dir = Path.Combine(ProductsRoot, _activeProductName, "decks");
            Directory.CreateDirectory(dir);
            return dir;
        }
        Directory.CreateDirectory(SharedDeckDir);
        return SharedDeckDir;
    }

    /// <summary>
    /// Saves current teach points for the active product to its positions.json file.
    /// Called after teach point changes so the product folder stays in sync.
    /// </summary>
    public void SaveActiveProductPositions()
    {
        if (string.IsNullOrEmpty(_activeProductName)) return;

        var product = _teachPointManager.GetProduct(_activeProductName);
        if (product == null) return;

        var productDir = Path.Combine(ProductsRoot, _activeProductName);
        if (!Directory.Exists(productDir)) return;

        var posPath = Path.Combine(productDir, "positions.json");
        File.WriteAllText(posPath, JsonSerializer.Serialize(product, JsonOptions));
    }

    private void SyncPositionsToTeachPointManager(string productName, TeachPointProduct product)
    {
        // Delete existing points for this product and re-create from file
        var existing = _teachPointManager.GetProduct(productName);
        if (existing != null)
        {
            foreach (var pt in existing.Points.ToList())
                _teachPointManager.DeletePoint(productName, pt.DeviceName, pt.PositionName);
        }

        foreach (var pt in product.Points)
        {
            _teachPointManager.CreatePointManual(productName, pt.DeviceName, pt.PositionName,
                pt.X, pt.Y, pt.Z, pt.U, pt.V, pt.W);
        }
    }

    private string ReadActiveProductName()
    {
        if (!File.Exists(ActiveConfigPath))
            return string.Empty;

        try
        {
            var json = File.ReadAllText(ActiveConfigPath);
            var config = JsonSerializer.Deserialize<ActiveProductConfig>(json, JsonOptions);
            return config?.ActiveProduct ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void WriteActiveProductName(string name)
    {
        Directory.CreateDirectory(ProductsRoot);
        var config = new ActiveProductConfig { ActiveProduct = name };
        File.WriteAllText(ActiveConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private void MigrateOldTeachPoints()
    {
        var oldDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "teach-points");

        if (!Directory.Exists(oldDir)) return;

        // Only migrate if products folder has no product subdirectories yet
        if (GetProductNames().Count > 0) return;

        var files = Directory.GetFiles(oldDir, "*.json")
            .Where(f => !Path.GetFileNameWithoutExtension(f)!
                .Equals("active", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return;

        _logSink?.Info($"Migrating {files.Count} old teach-point product(s) to products folder...");

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file)!;
            try
            {
                CreateProduct(name);

                // Copy positions file
                var destPos = Path.Combine(ProductsRoot, name, "positions.json");
                File.Copy(file, destPos, overwrite: true);

                // Copy shared recipes/processes/decks into the product folder
                CopyDirectoryContents(SharedRecipeDir, Path.Combine(ProductsRoot, name, "recipes"));
                CopyDirectoryContents(SharedProcessDir, Path.Combine(ProductsRoot, name, "processes"));
                CopyDirectoryContents(SharedDeckDir, Path.Combine(ProductsRoot, name, "decks"));

                _logSink?.Info($"  Migrated product: {name}");
            }
            catch (Exception ex)
            {
                _logSink?.Error($"  Migration failed for {name}: {ex.Message}");
            }
        }

        // If there was an active product in old system, load it
        var oldActive = _teachPointManager.ActiveProductName;
        if (!string.IsNullOrEmpty(oldActive) && Directory.Exists(Path.Combine(ProductsRoot, oldActive)))
        {
            _activeProductName = oldActive;
            WriteActiveProductName(oldActive);
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith(".")) continue; // skip dotfiles like .lastprogram
            var dest = Path.Combine(destDir, fileName);
            if (!File.Exists(dest))
                File.Copy(file, dest);
        }
    }
}
