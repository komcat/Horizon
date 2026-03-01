using Horizon.Common;
using Horizon.LaserController.Models;

namespace Horizon.LaserController;

public static class LaserControllerJsonLoader
{
    private record LaserControllerDevicesFile
    {
        public List<LaserControllerDeviceConfig> Devices { get; init; } = new();
    }

    public static int LoadDevices(LaserControllerManager manager, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<LaserControllerDevicesFile>(configDir, "laser_controller_devices");
        if (config?.Devices == null) return 0;

        int count = 0;
        foreach (var entry in config.Devices)
        {
            manager.AddDevice(entry);
            count++;
        }
        return count;
    }
}
