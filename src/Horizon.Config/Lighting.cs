using Horizon.Common;

namespace Horizon.Config;

/// <summary>One output channel of an LED lighting controller.</summary>
public record LightingChannelConfig
{
    /// <summary>Channel number as printed on the controller's front panel (CCS PD3: L1..L8 = 1..8).</summary>
    public int Channel { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// Colour label driving the call-outs on a group: "red" / "green" / "blue" (the
    /// "white" call-out mixes all three) or "collimate".
    /// </summary>
    public string Color { get; init; } = string.Empty;
    /// <summary>
    /// Logical light the channel belongs to (e.g. "main_camera_light"). Channels sharing a
    /// group are driven together as one light; an ungrouped channel is a light by its name.
    /// </summary>
    public string Group { get; init; } = string.Empty;
    /// <summary>
    /// Camera id this channel illuminates (e.g. "main_camera"), so UIs can show the
    /// selected camera's lights. Give every channel of a group the same camera; empty =
    /// not tied to a camera (shown with every camera).
    /// </summary>
    public string Camera { get; init; } = string.Empty;
    /// <summary>Initial status value (controller-native units) until the first read-back; not sent to hardware.</summary>
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

/// <summary>Loads / saves <c>lighting_devices.json</c>.</summary>
public static class LightingJsonLoader
{
    private sealed record LightingFile
    {
        public List<LightingControllerConfig> LightingControllers { get; init; } = new();
    }

    public static IReadOnlyList<LightingControllerConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<LightingFile>(configDir, "lighting_devices")?.LightingControllers
               ?? new List<LightingControllerConfig>();
    }

    public static void Save(IReadOnlyList<LightingControllerConfig> controllers, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "lighting_devices",
            new LightingFile { LightingControllers = controllers.ToList() });
    }
}
