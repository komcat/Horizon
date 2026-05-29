using Horizon.Common;

namespace Horizon.Config;

/// <summary>
/// A programmable power supply. <see cref="Type"/> selects the adapter:
/// Keysight E36xxx is reached over LXI (SCPI on a TCP socket); UNI-T UDP3305S
/// is USB/USBTMC over VISA. <see cref="Ip"/>/<see cref="Port"/> and
/// <see cref="VisaResource"/> are mutually exclusive.
/// </summary>
public record PowerSupplyConfig
{
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// e.g. <c>keysight_e36441a_direct</c>, <c>keysight_e36103b_direct</c> (LXI/TCP);
    /// <c>unit_t_udp3305s</c> (USB/VISA).
    /// </summary>
    public string Type { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public int Port { get; init; } = 5025;
    /// <summary>
    /// VISA resource string for USB/USBTMC supplies, e.g.
    /// <c>USB0::0x0483::0x5740::ADP3424500154::INSTR</c>. UNI-T enumerates with
    /// the STMicro USB VID 0x0483, not a UNI-T VID.
    /// </summary>
    public string VisaResource { get; init; } = string.Empty;
    /// <summary>Number of independent output channels.</summary>
    public int Channels { get; init; } = 1;
}

/// <summary>Loads / saves <c>power_supply_devices.json</c>.</summary>
public static class PowerSupplyJsonLoader
{
    private record PowerSupplyFile
    {
        public List<PowerSupplyConfig> Devices { get; init; } = new();
    }

    public static IReadOnlyList<PowerSupplyConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<PowerSupplyFile>(configDir, "power_supply_devices")?.Devices
               ?? new List<PowerSupplyConfig>();
    }

    public static void Save(IReadOnlyList<PowerSupplyConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "power_supply_devices",
            new PowerSupplyFile { Devices = devices.ToList() });
    }
}
