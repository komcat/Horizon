using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow;

/// <summary>
/// Loads / saves DUT test records — one JSON file per serial number in a
/// product's <c>dut\</c> directory. Horizon is the standalone system of record
/// for per-unit traceability (see <c>docs/recipe-migration.md</c> §7).
/// </summary>
internal sealed class DutStore
{
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly string _product;

    public DutStore(string productDir, string productName)
    {
        _dir = Path.Combine(productDir, "dut");
        _product = productName;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Serial numbers that have a record in this product.</summary>
    public IReadOnlyList<string> List()
        => Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*" + Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Loads the record for <paramref name="serial"/>, or creates a fresh one if
    /// none exists (or the file is unreadable).
    /// </summary>
    public DutRecord LoadOrCreate(string serial)
    {
        var path = PathFor(serial);
        if (File.Exists(path))
        {
            try
            {
                var record = JsonSerializer.Deserialize<DutRecord>(File.ReadAllText(path), JsonOptions);
                if (record != null) return record;
            }
            catch
            {
                // Unreadable record — fall through and start a fresh one.
            }
        }
        return new DutRecord { Product = _product, Serial = serial, CreatedUtc = DateTime.UtcNow };
    }

    /// <summary>Writes a record to disk, overwriting any prior file for that serial.</summary>
    public void Save(DutRecord record)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(record.Serial), JsonSerializer.Serialize(record, JsonOptions));
    }

    private string PathFor(string serial) => Path.Combine(_dir, Sanitize(serial) + Extension);

    /// <summary>Strips characters invalid in a file name from a serial number.</summary>
    private static string Sanitize(string serial)
    {
        var cleaned = new string(serial
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }
}
