using Horizon.Common;

namespace Horizon.Config;

/// <summary>
/// A TEC / laser-controller device — Thorlabs CLD101x (laser + TEC, over VISA
/// or TCP), standalone TED4015 TEC controller, or Thorlabs TTC-001 T-Cube TEC
/// (Kinesis SDK over USB by serial number). <see cref="ControllerType"/>
/// selects the adapter; VISA, TCP and Kinesis fields are mutually exclusive.
/// </summary>
public record TecDeviceConfig
{
    /// <summary>Optional free-text note carried in the config file.</summary>
    public string Comment { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// e.g. <c>CLD101x_Direct</c>, <c>CLD101x</c>, <c>TED4015_Direct</c>,
    /// <c>TTC001</c>.
    /// </summary>
    public string ControllerType { get; init; } = string.Empty;
    /// <summary>VISA resource string for direct-USB adapters.</summary>
    public string VisaResource { get; init; } = string.Empty;
    /// <summary>Host for TCP adapters (alternative to <see cref="VisaResource"/>).</summary>
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    /// <summary>
    /// 8-digit Kinesis USB serial number (e.g. <c>"87814387"</c>) for TTC-001.
    /// </summary>
    public string SerialNumber { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public double DefaultTemperature { get; init; } = 25.0;
    /// <summary>Default laser-diode current (mA); 0 for TEC-only controllers.</summary>
    public double DefaultLaserCurrent { get; init; }

    // --- TTC-001 / thermistor parameters (ignored by other adapters) ---

    /// <summary>
    /// Sensor mode for TTC-001: <c>"transducer"</c> (AD590/AD592),
    /// <c>"thermistor_20k"</c>, or <c>"thermistor_200k"</c>. Defaults to
    /// <c>"thermistor_20k"</c>.
    /// </summary>
    public string SensorMode { get; init; } = "thermistor_20k";
    /// <summary>Thermistor beta value (Kelvin) for the beta equation.</summary>
    public double ThermistorBeta { get; init; } = 3977.0;
    /// <summary>Nominal thermistor resistance at <see cref="ThermistorT0Celsius"/> (Ohms).</summary>
    public double ThermistorR0Ohms { get; init; } = 10000.0;
    /// <summary>Reference temperature for the thermistor R0 value (°C).</summary>
    public double ThermistorT0Celsius { get; init; } = 25.0;
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
