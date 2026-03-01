using Horizon.Common;
using Horizon.CurrentMeasurement.Models;

namespace Horizon.CurrentMeasurement;

public static class CurrentMeasurementJsonLoader
{
    private record CurrentMeasurementDevicesFile
    {
        public List<CurrentMeasurementDeviceConfig> Devices { get; init; } = new();
    }

    public static int LoadDevices(CurrentMeasurementManager manager, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<CurrentMeasurementDevicesFile>(configDir, "current_measurement_devices");
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
