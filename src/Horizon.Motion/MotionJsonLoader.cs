using Horizon.Common;
using Horizon.Motion.Models;

namespace Horizon.Motion;

public static class MotionJsonLoader
{
    private record MotionDevicesFile
    {
        public List<MotionDevice> Devices { get; init; } = new();
    }

    /// <summary>Reads the motor-device config, or an empty list if the file is absent.</summary>
    public static IReadOnlyList<MotionDevice> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        return ConfigLoader.LoadJson<MotionDevicesFile>(configDir, "motor_devices")?.Devices
               ?? new List<MotionDevice>();
    }

    /// <summary>Writes the motor-device config to <c>motor_devices.json</c>.</summary>
    public static void Save(IReadOnlyList<MotionDevice> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "motor_devices",
            new MotionDevicesFile { Devices = devices.ToList() });
    }

    public static int LoadDevices(UnifiedMotionManager manager, string? configDir = null)
    {
        var devices = Load(configDir);
        foreach (var device in devices)
            manager.AddDevice(device);
        return devices.Count;
    }
}
