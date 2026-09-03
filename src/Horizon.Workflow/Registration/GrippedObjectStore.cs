using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow.Registration;

/// <summary>
/// Per-product store of gripped objects — one JSON list at
/// <c>{productDir}\gripped_objects.json</c>, keyed by object name. Constructed against a
/// product directory; <c>ProcessService</c> discards and rebuilds it on product change.
/// </summary>
internal sealed class GrippedObjectStore
{
    private const string FileName = "gripped_objects.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<GrippedObject> _all;

    public GrippedObjectStore(string productDir)
    {
        _path = Path.Combine(productDir, FileName);
        _all = Load();
    }

    private List<GrippedObject> Load()
    {
        if (!File.Exists(_path)) return new List<GrippedObject>();
        try { return JsonSerializer.Deserialize<List<GrippedObject>>(File.ReadAllText(_path), JsonOptions) ?? new(); }
        catch { return new List<GrippedObject>(); }
    }

    public IReadOnlyList<GrippedObject> All
    {
        get { lock (_gate) return _all.ToArray(); }
    }

    public GrippedObject? Get(string name)
    {
        lock (_gate)
            return _all.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(GrippedObject obj)
    {
        lock (_gate)
        {
            _all.RemoveAll(o => string.Equals(o.Name, obj.Name, StringComparison.OrdinalIgnoreCase));
            _all.Add(obj);
            File.WriteAllText(_path, JsonSerializer.Serialize(_all, JsonOptions));
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var n = _all.RemoveAll(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return false;
            File.WriteAllText(_path, JsonSerializer.Serialize(_all, JsonOptions));
            return true;
        }
    }
}
