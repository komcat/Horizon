using System.Text.Json;
using System.Text.Json.Serialization;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// One product's named positions, persisted as <c>positions.json</c> in the
/// product directory. A named position is <c>(device, name)</c> → a
/// <see cref="MotionPosition"/> coordinate — the engine never sees the name.
/// </summary>
internal sealed class PositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly List<PositionRecord> _positions;
    private string _productName = string.Empty;

    public PositionStore(string productDir)
    {
        _path = Path.Combine(productDir, "positions.json");
        _positions = Load();
    }

    public IReadOnlyList<string> GetNames(string device)
        => _positions
            .Where(p => p.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public NamedPosition? Get(string device, string name)
    {
        var rec = Find(device, name);
        return rec == null
            ? null
            : new NamedPosition(rec.Device, rec.Name,
                new MotionPosition(rec.X, rec.Y, rec.Z, rec.U, rec.V, rec.W), rec.Velocity);
    }

    public void Set(string device, string name, MotionPosition pos, double velocity)
    {
        _positions.RemoveAll(p =>
            p.Device.Equals(device, StringComparison.OrdinalIgnoreCase) &&
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _positions.Add(new PositionRecord
        {
            Device = device,
            Name = name,
            X = pos.X, Y = pos.Y, Z = pos.Z, U = pos.U, V = pos.V, W = pos.W,
            Velocity = velocity,
        });
        Save();
    }

    public bool Remove(string device, string name)
    {
        var removed = _positions.RemoveAll(p =>
            p.Device.Equals(device, StringComparison.OrdinalIgnoreCase) &&
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save();
        return removed > 0;
    }

    private PositionRecord? Find(string device, string name)
        => _positions.FirstOrDefault(p =>
            p.Device.Equals(device, StringComparison.OrdinalIgnoreCase) &&
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private List<PositionRecord> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<PositionsFile>(File.ReadAllText(_path), JsonOptions);
            _productName = doc?.ProductName ?? string.Empty;
            return doc?.Positions ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path,
            JsonSerializer.Serialize(
                new PositionsFile { ProductName = _productName, Positions = _positions }, JsonOptions));
    }

    // Field names match the shared positions.json written by the legacy app:
    // points[] / deviceName / positionName. x..w match case-insensitively.
    private sealed record PositionsFile
    {
        [JsonPropertyName("productName")]
        public string ProductName { get; init; } = string.Empty;

        [JsonPropertyName("points")]
        public List<PositionRecord> Positions { get; init; } = new();
    }

    private sealed record PositionRecord
    {
        [JsonPropertyName("deviceName")]
        public string Device { get; init; } = string.Empty;

        [JsonPropertyName("positionName")]
        public string Name { get; init; } = string.Empty;
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public double U { get; init; }
        public double V { get; init; }
        public double W { get; init; }
        public double Velocity { get; init; }
    }
}
