using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow.Registration;

/// <summary>
/// Per-product store of part alignment profiles — one JSON list at
/// <c>{productDir}\part_alignments.json</c>, keyed by name. <c>ProcessService</c> discards
/// and rebuilds it on product change.
/// </summary>
internal sealed class PartAlignmentStore
{
    private const string FileName = "part_alignments.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,   // value tuples in PartSnapshot are never stored, but harmless
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<PartAlignment> _all;

    public PartAlignmentStore(string productDir)
    {
        _path = Path.Combine(productDir, FileName);
        _all = Load();
    }

    private List<PartAlignment> Load()
    {
        if (!File.Exists(_path)) return new List<PartAlignment>();
        try { return JsonSerializer.Deserialize<List<PartAlignment>>(File.ReadAllText(_path), JsonOptions) ?? new(); }
        catch { return new List<PartAlignment>(); }
    }

    public IReadOnlyList<PartAlignment> All
    {
        get { lock (_gate) return _all.ToArray(); }
    }

    public PartAlignment? Get(string name)
    {
        lock (_gate)
            return _all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(PartAlignment part)
    {
        lock (_gate)
        {
            _all.RemoveAll(p => string.Equals(p.Name, part.Name, StringComparison.OrdinalIgnoreCase));
            _all.Add(part);
            File.WriteAllText(_path, JsonSerializer.Serialize(_all, JsonOptions));
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var n = _all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return false;
            File.WriteAllText(_path, JsonSerializer.Serialize(_all, JsonOptions));
            return true;
        }
    }
}
