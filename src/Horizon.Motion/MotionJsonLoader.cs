using Horizon.Common;
using Horizon.Motion.Models;

namespace Horizon.Motion;

public static class MotionJsonLoader
{
    private record MotionDevicesFile
    {
        public List<MotionDevice> Devices { get; init; } = new();
    }

    public static int LoadDevices(UnifiedMotionManager manager, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<MotionDevicesFile>(configDir, "motor_devices");
        if (config?.Devices == null) return 0;

        int count = 0;
        foreach (var device in config.Devices)
        {
            manager.AddDevice(device);
            count++;
        }
        return count;
    }
}
