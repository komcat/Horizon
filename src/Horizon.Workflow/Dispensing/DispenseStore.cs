using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// Per-product store of named dispense routines — one JSON file per routine under
/// <c>{productDir}\dispense\{name}.json</c>. Mirrors <c>ProgramStore</c>'s directory-of-files
/// shape so the available routine names are just the file names. Constructed against a
/// product directory; <c>ProcessService</c> discards and rebuilds it on product change.
/// </summary>
internal sealed class DispenseStore
{
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;

    public DispenseStore(string productDir)
    {
        _dir = Path.Combine(productDir, "dispense");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Routine names (file stems), case-insensitive ordered.</summary>
    public IReadOnlyList<string> List()
        => Directory.GetFiles(_dir, "*" + Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Loads a routine, or null if missing / unparsable.</summary>
    public DispenseRoutine? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DispenseRoutine>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    /// <summary>Writes the routine (overwrites by name).</summary>
    public void Save(DispenseRoutine routine)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(routine.Name), JsonSerializer.Serialize(routine, JsonOptions));
    }

    /// <summary>Deletes a routine; returns false if it didn't exist.</summary>
    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string name) => Path.Combine(_dir, SafeName(name) + Extension);

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
