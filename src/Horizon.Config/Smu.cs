using Horizon.Common;

namespace Horizon.Config;

/// <summary>
/// A source-measure / current-measurement instrument — Keithley 6482
/// (dual picoammeter) or 2400 (SourceMeter). Source fields
/// (<see cref="DefaultVoltage"/> etc.) apply only to sourcing instruments
/// like the 2400.
/// </summary>
public record SmuDeviceConfig
{
    public string Name { get; init; } = string.Empty;
    /// <summary>e.g. <c>keithley_6482</c>, <c>keithley_2400</c>.</summary>
    public string Type { get; init; } = string.Empty;
    public string VisaResource { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int ChannelCount { get; init; } = 1;
    public List<int> EnabledChannels { get; init; } = new();

    /// <summary>Integration time in power-line cycles.</summary>
    public double MeasurementNplc { get; init; } = 1.0;
    public bool AutoRange { get; init; } = true;
    /// <summary>Fixed measurement range; used only when <see cref="AutoRange"/> is false.</summary>
    public double FixedRange { get; init; }

    // --- sourcing instruments only (e.g. Keithley 2400) ---
    public double DefaultVoltage { get; init; }
    public double DefaultCurrent { get; init; }
    public double DefaultCompliance { get; init; }
}

/// <summary>Loads / saves <c>smu_devices.json</c>.</summary>
public static class SmuJsonLoader
{
    private record SmuDevicesFile
    {
        public List<SmuDeviceConfig> Devices { get; init; } = new();
    }

    public static IReadOnlyList<SmuDeviceConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<SmuDevicesFile>(configDir, "smu_devices")?.Devices
               ?? new List<SmuDeviceConfig>();
    }

    public static void Save(IReadOnlyList<SmuDeviceConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "smu_devices",
            new SmuDevicesFile { Devices = devices.ToList() });
    }
}
