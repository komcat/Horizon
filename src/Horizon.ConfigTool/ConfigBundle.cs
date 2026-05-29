using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Horizon.ConfigTool;

/// <summary>One config file in the bundle manifest: a logical id and its on-disk filename.</summary>
internal sealed record BundleEntry(string Id, string FileName);

/// <summary>
/// The config-bundle document: a versioned envelope plus a logical-id → file-content
/// map. Each entry is the target file's JSON embedded verbatim (a <see cref="JsonNode"/>),
/// so every domain's exact format is preserved — including camera-config.json's plain
/// array with integer enums.
/// </summary>
internal sealed record ConfigBundleFile
{
    [JsonPropertyName("type")] public string Type { get; init; } = ConfigBundle.BundleType;
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; init; } = ConfigBundle.CurrentFormatVersion;
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; init; } = string.Empty;
    [JsonPropertyName("tool")] public string Tool { get; init; } = "Horizon.ConfigTool";
    [JsonPropertyName("profile")] public string? Profile { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("files")] public Dictionary<string, JsonNode?> Files { get; init; } = new();
}

/// <summary>
/// Reads/writes a transferable config bundle — a single JSON file holding the whole
/// config set. Keyed by a logical-id manifest (not on-disk filenames), so the bundle is
/// rename-stable and import is path-traversal-proof: only manifest filenames are ever written.
/// </summary>
internal static class ConfigBundle
{
    public const string BundleType = "horizon-config-bundle";
    public const int CurrentFormatVersion = 1;

    /// <summary>Logical id → on-disk filename. The single source of what the bundle covers.</summary>
    public static readonly BundleEntry[] Manifest =
    {
        new("motion", "motor_devices.json"),
        new("io", "io_devices.json"),
        new("ioWatch", "io_watch.json"),
        new("pneumatic", "pneumatic_slides.json"),
        new("laser", "laser_controller_devices.json"),
        new("measurement", "current_measurement_devices.json"),
        new("camera", "camera-config.json"),
        new("safety", "safety_watch.json"),
        new("interlocks", "motion_interlocks.json"),
        new("lighting", "lighting_devices.json"),
        new("tec", "tec_devices.json"),
        new("smu", "smu_devices.json"),
        new("powerSupply", "power_supply_devices.json"),
        new("tcp", "tcp_clients.json"),
        new("deviceLinks", "device_links.json"),
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The on-disk filename for a logical id, or null if the id is unknown.</summary>
    public static string? FileNameFor(string id) =>
        Manifest.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))?.FileName;

    /// <summary>
    /// Builds a bundle from every manifest file present in <paramref name="configDir"/>.
    /// Returns the bundle and the list of ids skipped because their file was missing.
    /// </summary>
    public static (ConfigBundleFile Bundle, List<string> Skipped) Build(
        string configDir, string? profile, string? description, string createdUtc)
    {
        var files = new Dictionary<string, JsonNode?>();
        var skipped = new List<string>();
        foreach (var entry in Manifest)
        {
            var path = Path.Combine(configDir, entry.FileName);
            if (!File.Exists(path)) { skipped.Add(entry.Id); continue; }
            files[entry.Id] = JsonNode.Parse(File.ReadAllText(path));
        }

        var bundle = new ConfigBundleFile
        {
            Type = BundleType,
            FormatVersion = CurrentFormatVersion,
            CreatedUtc = createdUtc,
            Tool = "Horizon.ConfigTool",
            Profile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Files = files,
        };
        return (bundle, skipped);
    }

    public static void Write(ConfigBundleFile bundle, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(bundle, WriteOptions));

    /// <summary>Reads + validates a bundle file. Throws on wrong type or an unsupported (newer) version.</summary>
    public static ConfigBundleFile Read(string path)
    {
        var bundle = JsonSerializer.Deserialize<ConfigBundleFile>(File.ReadAllText(path), ReadOptions)
                     ?? throw new InvalidDataException("File is empty or not a valid JSON bundle.");
        if (!string.Equals(bundle.Type, BundleType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not a Horizon config bundle (type = '{bundle.Type}').");
        if (bundle.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"Bundle formatVersion {bundle.FormatVersion} is newer than this tool supports " +
                $"({CurrentFormatVersion}). Update Horizon.ConfigTool.");
        return bundle;
    }

    /// <summary>Serializes one embedded file node back to indented JSON for writing to disk.</summary>
    public static string SerializeNode(JsonNode? node) => node?.ToJsonString(WriteOptions) ?? "null";
}
