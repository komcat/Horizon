using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow;

/// <summary>
/// Loads / saves <c>.hvp</c> vision profiles in a product's <c>vision\</c> directory,
/// and owns the per-template binaries under <c>vision\templates\{uid}\</c>
/// (<c>template.ncm</c> + <c>template.png</c>). Mirrors <see cref="DeckStore"/>.
/// </summary>
internal sealed class VisionProfileStore
{
    private const string Extension = ".hvp";
    private const string ModelFile = "template.ncm";
    private const string ImageFile = "template.png";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _dir;
    private readonly string _templatesDir;
    private readonly string _activePath;

    public VisionProfileStore(string productDir)
    {
        _dir = Path.Combine(productDir, "vision");
        _templatesDir = Path.Combine(_dir, "templates");
        _activePath = Path.Combine(_dir, "active.json");
        Directory.CreateDirectory(_templatesDir);
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

    public VisionProfile? Load(string name)
    {
        var path = Path.Combine(_dir, name + Extension);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<VisionProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(VisionProfile profile)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, profile.Name + Extension);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public void Delete(string name)
    {
        // Drop the profile's template binaries too, if it had any.
        var profile = Load(name);
        if (profile?.Template is { Uid.Length: > 0 } t) DeleteTemplate(t.Uid);

        var path = Path.Combine(_dir, name + Extension);
        if (File.Exists(path)) File.Delete(path);
        if (string.Equals(ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
            SetActive(null);
    }

    /// <summary>Name of the last-used profile for this product, or null if none recorded.</summary>
    public string? ActiveProfile
    {
        get
        {
            if (!File.Exists(_activePath)) return null;
            try
            {
                var doc = JsonSerializer.Deserialize<ActiveDoc>(File.ReadAllText(_activePath), JsonOptions);
                return string.IsNullOrEmpty(doc?.ActiveProfile) ? null : doc!.ActiveProfile;
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetActive(string? name)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_activePath,
            JsonSerializer.Serialize(new ActiveDoc { ActiveProfile = name ?? string.Empty }, JsonOptions));
    }

    // --- template binaries ---

    /// <summary>A fresh template id. Retaking a template always mints a new one.</summary>
    public static string NewTemplateUid() => "tpl-" + Guid.NewGuid().ToString("N");

    /// <summary>Ensures the per-template directory exists and returns it.</summary>
    public string EnsureTemplateDir(string uid)
    {
        var dir = Path.Combine(_templatesDir, uid);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ModelPath(string uid) => Path.Combine(_templatesDir, uid, ModelFile);

    public string ImagePath(string uid) => Path.Combine(_templatesDir, uid, ImageFile);

    public void DeleteTemplate(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        var dir = Path.Combine(_templatesDir, uid);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private sealed record ActiveDoc
    {
        public string ActiveProfile { get; init; } = string.Empty;
    }
}
