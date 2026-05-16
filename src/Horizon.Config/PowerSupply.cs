using Horizon.Common;

namespace Horizon.Config;

/// <summary>
/// A programmable power supply — e.g. Keysight E36441A / E36103B — reached
/// over LXI (SCPI on a TCP socket).
/// </summary>
public record PowerSupplyConfig
{
    public string Name { get; init; } = string.Empty;
    /// <summary>e.g. <c>keysight_e36441a_direct</c>, <c>keysight_e36103b_direct</c>.</summary>
    public string Type { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public int Port { get; init; } = 5025;
    /// <summary>Number of independent output channels.</summary>
    public int Channels { get; init; } = 1;
}

/// <summary>Loads / saves <c>power_supply_config.json</c>.</summary>
public static class PowerSupplyJsonLoader
{
    private record PowerSupplyFile
    {
        public List<PowerSupplyConfig> Devices { get; init; } = new();
    }

    public static IReadOnlyList<PowerSupplyConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<PowerSupplyFile>(configDir, "power_supply_config")?.Devices
               ?? new List<PowerSupplyConfig>();
    }

    public static void Save(IReadOnlyList<PowerSupplyConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "power_supply_config",
            new PowerSupplyFile { Devices = devices.ToList() });
    }
}
