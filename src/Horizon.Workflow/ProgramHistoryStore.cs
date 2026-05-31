using System.Text.Json;

namespace Horizon.Workflow;

/// <summary>
/// Loads / appends per-program revision history — one JSON file per program in a
/// product's <c>processes\history\</c> directory. Each <see cref="ProcessService.SaveProgram"/>
/// appends an operator-stamped snapshot, capped at the most recent
/// <see cref="MaxVersions"/>.
/// </summary>
internal sealed class ProgramHistoryStore
{
    private const string Extension = ".json";
    private const int MaxVersions = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;

    public ProgramHistoryStore(string productDir)
    {
        _dir = Path.Combine(productDir, "processes", "history");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Reads a program's history, or an empty history if none exists yet.</summary>
    public ProgramHistory Load(string program)
    {
        var path = PathFor(program);
        if (File.Exists(path))
        {
            try
            {
                var h = JsonSerializer.Deserialize<ProgramHistory>(File.ReadAllText(path), JsonOptions);
                if (h != null) return h;
            }
            catch
            {
                // Unreadable history — start fresh rather than block saving.
            }
        }
        return new ProgramHistory { Program = program };
    }

    /// <summary>
    /// Appends a new revision of <paramref name="snapshot"/> stamped with the
    /// operator, returning the recorded version. Caps the kept history at
    /// <see cref="MaxVersions"/>.
    /// </summary>
    public ProgramVersion Append(WorkflowProgram snapshot, OperatorContext? op)
    {
        var history = Load(snapshot.Name);
        var version = new ProgramVersion
        {
            Version = (history.Latest?.Version ?? 0) + 1,
            SavedUtc = DateTime.UtcNow,
            OperatorId = op?.UserId,
            OperatorName = op?.DisplayName,
            StationId = op?.StationId,
            Snapshot = snapshot,
        };
        history.Versions.Add(version);
        while (history.Versions.Count > MaxVersions)
            history.Versions.RemoveAt(0);

        File.WriteAllText(PathFor(snapshot.Name), JsonSerializer.Serialize(history, JsonOptions));
        return version;
    }

    private string PathFor(string program) => Path.Combine(_dir, Sanitize(program) + Extension);

    private static string Sanitize(string name)
    {
        var cleaned = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }
}
