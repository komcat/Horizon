using System.Text.Json;
using System.Text.Json.Serialization;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>What <see cref="PositionStore.Remove"/> did.</summary>
internal enum PositionRemoveOutcome
{
    /// <summary>No such position.</summary>
    NotFound,

    /// <summary>The position was deleted (base selected, or it existed only in the variant).</summary>
    Removed,

    /// <summary>The variant's override was deleted — the position reads from base again.</summary>
    Reverted,

    /// <summary>A variant is selected and the position exists only in base — nothing was removed.</summary>
    BaseOnly,
}

/// <summary>
/// One product's named positions. A named position is <c>(device, name)</c> → a
/// <see cref="MotionPosition"/> coordinate — the engine never sees the name.
/// <para>
/// <b>Base</b> positions are <c>positions.json</c> in the product directory. A
/// <b>variant</b> is a file <c>position_variants\{variant}.json</c> holding only the
/// positions that differ: with a variant selected, a lookup reads the variant first and
/// falls back to base, and every write goes to the variant — base is never changed
/// while a variant is selected.
/// </para>
/// </summary>
internal sealed class PositionStore
{
    /// <summary>Product sub-directory holding one file per variant.</summary>
    public const string VariantDirName = "position_variants";

    /// <summary>The name shown for "no variant" — not usable as a variant name.</summary>
    public const string BaseName = "base";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly string _variantDir;
    private readonly List<PositionRecord> _positions;      // base
    private List<PositionRecord> _overrides = new();       // the selected variant's records
    private string? _variant;                              // null = base
    private string _productName = string.Empty;

    public PositionStore(string productDir)
    {
        _path = Path.Combine(productDir, "positions.json");
        _variantDir = Path.Combine(productDir, VariantDirName);
        _positions = LoadBase();
    }

    // --- Variants ---

    /// <summary>The selected variant, or null when base is selected.</summary>
    public string? Variant => _variant;

    public IReadOnlyList<string> GetVariants()
    {
        if (!Directory.Exists(_variantDir)) return Array.Empty<string>();
        return Directory.GetFiles(_variantDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool VariantExists(string name) => File.Exists(VariantPath(name));

    public static bool IsValidVariantName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && !name.Trim().Equals(BaseName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a variant file holding a copy of the currently selected variant's
    /// overrides (none when base is selected). Does not select it.
    /// </summary>
    public void CreateVariant(string name)
        => WriteFile(VariantPath(name), new List<PositionRecord>(_overrides));

    /// <summary>Deletes a variant file; a selected variant is deselected (back to base) first.</summary>
    public void DeleteVariant(string name)
    {
        if (string.Equals(_variant, name, StringComparison.OrdinalIgnoreCase))
            SelectVariant(null, out _);
        var path = VariantPath(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Selects a variant (null = base). Fails — leaving the selection unchanged — when the
    /// variant's file is missing or unreadable: silently falling back to base would send
    /// the machine to the wrong positions.
    /// </summary>
    public bool SelectVariant(string? name, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            _variant = null;
            _overrides = new();
            return true;
        }

        var path = VariantPath(name);
        if (!File.Exists(path))
        {
            error = $"Position variant '{name}' not found.";
            return false;
        }
        try
        {
            var doc = JsonSerializer.Deserialize<PositionsFile>(File.ReadAllText(path), JsonOptions);
            _overrides = doc?.Positions ?? new();
            _variant = Path.GetFileNameWithoutExtension(path);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"Position variant '{name}' could not be read: {ex.Message}";
            return false;
        }
    }

    /// <summary>True when the selected variant holds its own value for this position.</summary>
    public bool IsOverridden(string device, string name)
        => _variant != null && Find(_overrides, device, name) != null;

    private string VariantPath(string name) => Path.Combine(_variantDir, name.Trim() + ".json");

    // --- Positions ---

    public IReadOnlyList<string> GetNames(string device)
        => _positions.Concat(_overrides)
            .Where(p => p.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public NamedPosition? Get(string device, string name)
    {
        var rec = Resolve(device, name);
        return rec == null
            ? null
            : new NamedPosition(rec.Device, rec.Name,
                new MotionPosition(rec.X, rec.Y, rec.Z, rec.U, rec.V, rec.W), rec.Velocity,
                string.IsNullOrEmpty(rec.Camera) ? null : rec.Camera, rec.ExposureUs);
    }

    /// <summary>
    /// Upserts coordinates + velocity, <b>preserving</b> any existing camera /
    /// exposure on the position. Used by teach and the in-program update steps,
    /// which change only the pose. Written to the selected variant, else base.
    /// </summary>
    public void Set(string device, string name, MotionPosition pos, double velocity)
    {
        var existing = Resolve(device, name);
        Upsert(device, name, pos, velocity, existing?.Camera ?? string.Empty, existing?.ExposureUs);
    }

    /// <summary>Upserts every field, including camera / exposure — used by the explicit position editor.</summary>
    public void SetFull(string device, string name, MotionPosition pos, double velocity, string? camera, double? exposureUs)
        => Upsert(device, name, pos, velocity, camera ?? string.Empty, exposureUs);

    private void Upsert(string device, string name, MotionPosition pos, double velocity, string camera, double? exposureUs)
    {
        var target = _variant == null ? _positions : _overrides;
        target.RemoveAll(p => Matches(p, device, name));
        target.Add(new PositionRecord
        {
            Device = device,
            Name = name,
            X = pos.X, Y = pos.Y, Z = pos.Z, U = pos.U, V = pos.V, W = pos.W,
            Velocity = velocity,
            Camera = camera,
            ExposureUs = exposureUs,
        });
        Save();
    }

    /// <summary>
    /// Base selected: deletes the position. Variant selected: deletes only the variant's
    /// override (the position reverts to base, or disappears if base never had it); a
    /// position that exists only in base is left alone.
    /// </summary>
    public PositionRemoveOutcome Remove(string device, string name)
    {
        if (_variant == null)
        {
            if (_positions.RemoveAll(p => Matches(p, device, name)) == 0)
                return PositionRemoveOutcome.NotFound;
            Save();
            return PositionRemoveOutcome.Removed;
        }

        var inBase = Find(_positions, device, name) != null;
        if (_overrides.RemoveAll(p => Matches(p, device, name)) == 0)
            return inBase ? PositionRemoveOutcome.BaseOnly : PositionRemoveOutcome.NotFound;
        Save();
        return inBase ? PositionRemoveOutcome.Reverted : PositionRemoveOutcome.Removed;
    }

    private PositionRecord? Resolve(string device, string name)
        => Find(_overrides, device, name) ?? Find(_positions, device, name);

    private static PositionRecord? Find(List<PositionRecord> list, string device, string name)
        => list.FirstOrDefault(p => Matches(p, device, name));

    private static bool Matches(PositionRecord p, string device, string name)
        => p.Device.Equals(device, StringComparison.OrdinalIgnoreCase) &&
           p.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private List<PositionRecord> LoadBase()
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

    /// <summary>Writes the layer that takes edits — the selected variant's file, else positions.json.</summary>
    private void Save()
    {
        if (_variant == null) WriteFile(_path, _positions);
        else WriteFile(VariantPath(_variant), _overrides);
    }

    private void WriteFile(string path, List<PositionRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(
                new PositionsFile { ProductName = _productName, Positions = records }, JsonOptions));
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

        /// <summary>Camera id whose exposure this position drives (empty = none).</summary>
        [JsonPropertyName("camera")]
        public string Camera { get; init; } = string.Empty;

        /// <summary>Target exposure in microseconds applied on recall (null = leave unchanged).</summary>
        [JsonPropertyName("exposureUs")]
        public double? ExposureUs { get; init; }
    }
}
