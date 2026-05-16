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
        public string DeviceType { get; init; } = "IOModule";
        public List<IOPinConfig> InputPins { get; init; } = new();
        public List<IOPinConfig> OutputPins { get; init; } = new();
    }

    /// <summary>Reads the IO-device config, or an empty list if the file is absent.</summary>
    public static IReadOnlyList<IODeviceConfig> Load(string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        var file = ConfigLoader.LoadJson<IODevicesFile>(configDir, "io_devices");
        if (file?.Devices == null) return new List<IODeviceConfig>();
        return file.Devices.Select(ToConfig).ToList();
    }

    /// <summary>Writes the IO-device config to <c>io_devices.json</c>.</summary>
    public static void Save(IReadOnlyList<IODeviceConfig> devices, string? configDir = null)
    {
        configDir ??= ConfigLoader.GetConfigDirectory();
        ConfigLoader.SaveJson(configDir, "io_devices",
            new IODevicesFile { Devices = devices.Select(ToEntry).ToList() });
    }

    public static int LoadDevices(EziIOManager manager, string? configDir = null)
    {
        var devices = Load(configDir);
        foreach (var device in devices)
            manager.AddDevice(device);
        return devices.Count;
    }

    /// <summary>Maps the flat JSON entry to the nested runtime config.</summary>
    private static IODeviceConfig ToConfig(IODeviceJsonEntry entry)
    {
        var devType = Enum.TryParse<EziDeviceType>(entry.DeviceType, true, out var dt)
            ? dt : EziDeviceType.MotorIO;

        return new IODeviceConfig
        {
            Device = new EziIODeviceConfig
            {
                DeviceId = entry.DeviceId,
                Name = entry.Name,
                IpAddress = entry.IpAddress,
                InputCount = entry.InputCount,
                OutputCount = entry.OutputCount,
                Enabled = entry.Enabled,
                DeviceType = devType
            },
            InputPins = entry.InputPins,
            OutputPins = entry.OutputPins
        };
    }

    /// <summary>Maps the nested runtime config back to the flat JSON entry.</summary>
    private static IODeviceJsonEntry ToEntry(IODeviceConfig config) => new()
    {
        DeviceId = config.Device.DeviceId,
        Name = config.Device.Name,
        IpAddress = config.Device.IpAddress,
        InputCount = config.Device.InputCount,
        OutputCount = config.Device.OutputCount,
        Enabled = config.Device.Enabled,
        DeviceType = config.Device.DeviceType.ToString(),
        InputPins = config.InputPins,
        OutputPins = config.OutputPins
    };
}
