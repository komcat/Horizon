namespace Horizon.Workflow.Recipe;

/// <summary>
/// Loads / saves <c>.hrec</c> recipe scripts in a product's <c>recipes\</c>
/// directory. Recipes are plain source text — no serialization.
/// </summary>
internal sealed class RecipeStore
{
    private const string Extension = ".hrec";

    private readonly string _dir;

    public RecipeStore(string productDir)
    {
        _dir = Path.Combine(productDir, "recipes");
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<string> List()
        => Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*" + Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    /// <summary>Reads a recipe's source text, or null if it does not exist.</summary>
    public string? Load(string name)
    {
        var path = Path.Combine(_dir, name + Extension);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Save(string name, string source)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, name + Extension), source ?? string.Empty);
    }
}
