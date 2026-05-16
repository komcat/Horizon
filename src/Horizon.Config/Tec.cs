using Horizon.Common;

namespace Horizon.Config;

/// <summary>
/// A TEC / laser-controller device — Thorlabs CLD101x (laser + TEC, over VISA
/// or TCP) or a standalone TED4015 TEC controller. <see cref="ControllerType"/>
/// selects the adapter; VISA and TCP fields are mutually exclusive.
/// </summary>
public record TecDeviceConfig
{
    /// <summary>Optional free-text note carried in the config file.</summary>
    public string Comment { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    /// <summary>e.g. <c>CLD101x_Direct</c>, <c>CLD101x</c>, <c>TED4015_Direct</c>.</summary>
    public string ControllerType { get; init; } = string.Empty;
    /// <summary>VISA resource string for direct-USB adapters.</summary>
    public string VisaResource { get; init; } = string.Empty;
    /// <summary>Host for TCP adapters (alternative to <see cref="VisaResource"/>).</summary>
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public bool Enabled { get; init; } = true;
    public double DefaultTemperature { get; init; } = 25.0;
    /// <summary>Default laser-diode current (mA); 0 for TEC-only controllers.</summary>
    public double DefaultLaserCurrent { get; init; }
}

/// <summary>Loads / saves <c>tec_devices.json</c>.</summary>
public static class TecJsonLoader
{
    private record TecDevicesFile
    {
        public List<TecDeviceConfig> TecDevices { get; init; } = new();
    }

    public static IReadOnlyList<TecDeviceConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<TecDevicesFile>(configDir, "tec_devices")?.TecDevices
               ?? new List<TecDeviceConfig>();
    }

    public static void Save(IReadOnlyList<TecDeviceConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "tec_devices",
            new TecDevicesFile { TecDevices = devices.ToList() });
    }
}
