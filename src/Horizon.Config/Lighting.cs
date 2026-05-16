using Horizon.Common;

namespace Horizon.Config;

/// <summary>One output channel of an LED lighting controller.</summary>
public record LightingChannelConfig
{
    public int Channel { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Free-text colour label (e.g. "white", "red"); empty if unused.</summary>
    public string Color { get; init; } = string.Empty;
    /// <summary>Logical group the channel belongs to (e.g. "light1").</summary>
    public string Group { get; init; } = string.Empty;
    public int DefaultIntensity { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// An LED lighting controller (e.g. CCS_PD3) — a network device with a fixed
/// set of intensity-controlled channels.
/// </summary>
public record LightingControllerConfig
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string ControllerType { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public bool Enabled { get; init; } = true;
    public List<LightingChannelConfig> Channels { get; init; } = new();
}

/// <summary>Loads / saves <c>lighting_config.json</c>.</summary>
public static class LightingJsonLoader
{
    private record LightingFile
    {
        public List<LightingControllerConfig> LightingControllers { get; init; } = new();
    }

    public static IReadOnlyList<LightingControllerConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<LightingFile>(configDir, "lighting_config")?.LightingControllers
               ?? new List<LightingControllerConfig>();
    }

    public static void Save(IReadOnlyList<LightingControllerConfig> controllers, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "lighting_config",
            new LightingFile { LightingControllers = controllers.ToList() });
    }
}
