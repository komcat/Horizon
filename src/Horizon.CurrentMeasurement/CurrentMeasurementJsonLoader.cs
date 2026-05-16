using Horizon.Common;
using Horizon.CurrentMeasurement.Models;

namespace Horizon.CurrentMeasurement;

public static class CurrentMeasurementJsonLoader
{
    private record CurrentMeasurementDevicesFile
    {
        public List<CurrentMeasurementDeviceConfig> Devices { get; init; } = new();
    }

    /// <summary>Reads the current-measurement config, or an empty list if the file is absent.</summary>
    public static IReadOnlyList<CurrentMeasurementDeviceConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<CurrentMeasurementDevicesFile>(configDir, "current_measurement_devices")?.Devices
               ?? new List<CurrentMeasurementDeviceConfig>();
    }

    /// <summary>Writes the current-measurement config to <c>current_measurement_devices.json</c>.</summary>
    public static void Save(IReadOnlyList<CurrentMeasurementDeviceConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "current_measurement_devices",
            new CurrentMeasurementDevicesFile { Devices = devices.ToList() });
    }

    public static int LoadDevices(CurrentMeasurementManager manager, string? configDir = null)
    {
        var devices = Load(configDir);
        foreach (var entry in devices)
            manager.AddDevice(entry);
        return devices.Count;
    }
}
