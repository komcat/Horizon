using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horizon.Workflow;

/// <summary>
/// One DUT's labelled SiPhOG snapshots — the whole <c>siphog_data\{serial}.json</c> file.
/// <see cref="Sets"/> is append-only and chronological; re-using a label adds another set,
/// and readers take the latest set per label.
/// </summary>
public sealed class SiphogSnapshotFile
{
    public const string CurrentSchema = "horizon.siphog-snapshots/1";

    public string Schema { get; set; } = CurrentSchema;
    public string Serial { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;

    /// <summary>Unit per value key (<c>sagPwrV</c> → <c>V</c>), so the file reads on its own.</summary>
    public Dictionary<string, string> Units { get; set; } = new();

    public List<SiphogSnapshotSet> Sets { get; set; } = new();

    /// <summary>
    /// The sets grouped into runs (one pass of the unit through the process), oldest first,
    /// numbered from 1. Sets written before run tracking (no <see cref="SiphogSnapshotSet.RunId"/>)
    /// form one untracked run.
    /// </summary>
    public IReadOnlyList<SiphogSnapshotRun> Runs()
        => Sets.GroupBy(s => s.RunId ?? string.Empty, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Sets: g.OrderBy(s => s.Timestamp).ToList()))
            .OrderBy(g => g.Sets[0].Timestamp)
            .Select((g, i) => new SiphogSnapshotRun(
                i + 1,
                g.Key.Length == 0 ? null : g.Key,
                g.Sets[0].RunStarted ?? g.Sets[0].Timestamp,
                g.Sets))
            .ToList();
}

/// <summary>One run's sets (chronological). <see cref="Id"/> null = sets saved before run tracking.</summary>
public sealed record SiphogSnapshotRun(int Number, string? Id, DateTimeOffset Started, IReadOnlyList<SiphogSnapshotSet> Sets)
{
    public bool Tracked => Id != null;
}

/// <summary>One labelled snapshot: averaged SiPhOG values over a short sampling window.</summary>
public sealed class SiphogSnapshotSet
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Local time with offset — operator-readable and unambiguous.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// The run (one pass of the unit through the process) this set belongs to — a new id each time
    /// the serial is entered (<c>dut.promptSerial</c>, an up-front serial, a Run-page DUT start), so a
    /// re-run of the same serial never mixes with the earlier attempt. Null in files written before
    /// run tracking.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>When that run began (serial entered); null when <see cref="RunId"/> is null.</summary>
    public DateTimeOffset? RunStarted { get; set; }

    public SnapshotOperator? Operator { get; set; }
    public string? Station { get; set; }
    public SnapshotSource? Source { get; set; }
    public SnapshotWindow Window { get; set; } = new();

    /// <summary>Per value key (signal key without the <c>siphog.</c> prefix). A key that could
    /// not be read in the window is omitted.</summary>
    public Dictionary<string, SignalStats> Values { get; set; } = new();

    /// <summary>SAG PWR mean as % of the target SAG PWR mean; null when either is missing / target is 0.</summary>
    public double? SagPercentOfTarget { get; set; }

    public string? Note { get; set; }
}

public sealed record SnapshotOperator(string Id, string Name, string? Level);

/// <summary>What produced the snapshot (program + block, or a script).</summary>
public sealed record SnapshotSource(string? Program, string? Step);

public sealed class SnapshotWindow
{
    public int RequestedMs { get; set; }
    public int ActualMs { get; set; }
    public int Samples { get; set; }
}

public sealed record SignalStats(double Mean, double Min, double Max, double Std);

/// <summary>
/// A request to take one labelled SiPhOG snapshot (<c>siphog.saveSnapshot</c> /
/// <c>machine.siphog.save</c>). <see cref="Serial"/> null = the active DUT serial.
/// </summary>
public sealed record SiphogSnapshotRequest(
    string Label, int WindowMs, string? Note = null, string? Serial = null,
    string? Program = null, string? Step = null);

/// <summary>
/// Loads / appends SiPhOG snapshot files — one JSON file per serial in a product's
/// <c>siphog_data\</c> directory. Not cloned with the product (DUT history, like <c>dut\</c>).
/// Writes go to a temp file then replace the original, so a crash never leaves a half file.
/// </summary>
internal sealed class SiphogSnapshotStore
{
    public const string FolderName = "siphog_data";
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "µA" / "°C" readable
    };

    private readonly string _dir;
    private readonly string _product;
    private readonly object _gate = new();

    public SiphogSnapshotStore(string productDir, string productName)
    {
        _dir = Path.Combine(productDir, FolderName);
        _product = productName;
    }

    /// <summary>The product's <c>siphog_data\</c> folder (may not exist until the first snapshot is saved).</summary>
    public string DirectoryPath => _dir;

    /// <summary>Serials that have a snapshot file in this product.</summary>
    public IReadOnlyList<string> List()
        => Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*" + Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    /// <summary>The file for <paramref name="serial"/>, or null when none exists / it is unreadable.</summary>
    public SiphogSnapshotFile? Load(string serial)
    {
        var path = PathFor(serial);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SiphogSnapshotFile>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    /// <summary>
    /// Appends one set to the serial's file (creating it). An existing file that can't be
    /// parsed is kept aside as <c>{serial}.json.corrupt-{ticks}</c> rather than overwritten.
    /// </summary>
    public void Append(string serial, SiphogSnapshotSet set, IReadOnlyDictionary<string, string> units)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(serial);

            SiphogSnapshotFile? file = null;
            if (File.Exists(path))
            {
                try { file = JsonSerializer.Deserialize<SiphogSnapshotFile>(File.ReadAllText(path), JsonOptions); }
                catch { file = null; }
                if (file == null)
                    File.Move(path, $"{path}.corrupt-{DateTime.UtcNow.Ticks}");
            }

            file ??= new SiphogSnapshotFile { Serial = serial, Product = _product };
            file.Schema = SiphogSnapshotFile.CurrentSchema;
            foreach (var (k, u) in units) file.Units[k] = u;
            file.Sets.Add(set);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private string PathFor(string serial) => Path.Combine(_dir, Sanitize(serial) + Extension);

    /// <summary>Strips characters invalid in a file name from a serial number (same rule as <c>DutStore</c>).</summary>
    private static string Sanitize(string serial)
    {
        var cleaned = new string(serial
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }
}
