using System.Text.Json;

namespace Horizon.Workflow;

/// <summary>
/// Loads / saves <c>.hwf</c> process programs in a product's <c>processes\</c>
/// directory.
/// </summary>
internal sealed class ProgramStore
{
    private const string Extension = ".hwf";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;

    public ProgramStore(string productDir)
    {
        _dir = Path.Combine(productDir, "processes");
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

    public WorkflowProgram? Load(string name)
    {
        var path = Path.Combine(_dir, name + Extension);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkflowProgram>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(WorkflowProgram program)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, program.Name + Extension);
        File.WriteAllText(path, JsonSerializer.Serialize(program, JsonOptions));
    }
}
