using Horizon.Common;
using Horizon.LaserController.Models;

namespace Horizon.LaserController;

public static class LaserControllerJsonLoader
{
    private record LaserControllerDevicesFile
    {
        public List<LaserControllerDeviceConfig> Devices { get; init; } = new();
    }

    /// <summary>Reads the laser-controller config, or an empty list if the file is absent.</summary>
    public static IReadOnlyList<LaserControllerDeviceConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<LaserControllerDevicesFile>(configDir, "laser_controller_devices")?.Devices
               ?? new List<LaserControllerDeviceConfig>();
    }

    /// <summary>Writes the laser-controller config to <c>laser_controller_devices.json</c>.</summary>
    public static void Save(IReadOnlyList<LaserControllerDeviceConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "laser_controller_devices",
            new LaserControllerDevicesFile { Devices = devices.ToList() });
    }

    public static int LoadDevices(LaserControllerManager manager, string? configDir = null)
    {
        var devices = Load(configDir);
        foreach (var entry in devices)
            manager.AddDevice(entry);
        return devices.Count;
    }
}
