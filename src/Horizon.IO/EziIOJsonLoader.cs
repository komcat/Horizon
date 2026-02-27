using System.Text.Json;
using Horizon.Common;
using Horizon.IO.Models;

namespace Horizon.IO;

public static class EziIOJsonLoader
{
    private record IODevicesFile
    {
        public List<IODeviceJsonEntry> Devices { get; init; } = new();
    }

    private record IODeviceJsonEntry
    {
        public int DeviceId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string IpAddress { get; init; } = string.Empty;
        public int InputCount { get; init; }
        public int OutputCount { get; init; }
        public bool Enabled { get; init; } = true;
        public List<IOPinConfig> InputPins { get; init; } = new();
        public List<IOPinConfig> OutputPins { get; init; } = new();
    }

    public static int LoadDevices(EziIOManager manager, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var config = ConfigLoader.LoadJson<IODevicesFile>(configDir, "io_devices");
        if (config?.Devices == null) return 0;

        int count = 0;
        foreach (var entry in config.Devices)
        {
            var deviceConfig = new IODeviceConfig
            {
                Device = new EziIODeviceConfig
                {
                    DeviceId = entry.DeviceId,
                    Name = entry.Name,
                    IpAddress = entry.IpAddress,
                    InputCount = entry.InputCount,
                    OutputCount = entry.OutputCount,
                    Enabled = entry.Enabled
                },
                InputPins = entry.InputPins,
                OutputPins = entry.OutputPins
            };

            manager.AddDevice(deviceConfig);
            count++;
        }
        return count;
    }
}
