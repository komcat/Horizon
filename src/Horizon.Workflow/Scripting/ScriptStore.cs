namespace Horizon.Workflow.Scripting;

/// <summary>
/// Loads / saves <c>.moon</c> procedural scripts in a product's <c>scripts\</c>
/// directory. Scripts are plain Lua source text — no serialization. Mirrors
/// <c>RecipeStore</c>; promoted to a Layer 2 facade method in a later phase.
/// </summary>
public sealed class ScriptStore
{
    private const string Extension = ".moon";

    private readonly string _dir;

    public ScriptStore(string productDir)
    {
        _dir = Path.Combine(productDir, "scripts");
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

    /// <summary>Reads a script's source text, or null if it does not exist.</summary>
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
